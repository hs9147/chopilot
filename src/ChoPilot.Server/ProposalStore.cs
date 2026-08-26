using System.Collections.Concurrent;
using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>
/// 제안 결정 요청. 사유는 선택이지만 <b>다음 자체 평가의 유일한 질적 입력</b>이라
/// 기각할 때는 적어 두는 편이 낫다 — 숫자만으로는 왜 기각됐는지 되짚을 수 없다.
/// </summary>
public sealed record ProposalDecision(bool Accept, int? Rating = null, string? Note = null);

/// <summary>종류별 성적. <see cref="MeanRating"/>이 기준을 고치는 입력이다.</summary>
public sealed record KindOutcome(
    string Kind, int Proposed, int Accepted, int Rejected, int Rated, double? MeanRating)
{
    public int Decided => Accepted + Rejected;

    /// <summary>
    /// 결정된 것 중 채택 비율. 참고 지표일 뿐 <b>기준을 고치는 데 쓰지 않는다</b> —
    /// "근거는 맞지만 지금 손댈 수 없다"는 기각이 흔해서 유용성과 어긋난다.
    /// </summary>
    public double? AcceptanceRate => Decided == 0 ? null : (double)Accepted / Decided;
}

/// <summary>
/// 업무 개선 제안 저장소 + 선정 기준의 버전 이력.
///
/// <para>
/// 기준을 <b>덮어쓰지 않고 쌓는다</b>. 기준이 조용히 바뀌면 "왜 저번엔 제안됐는데 이번엔
/// 안 되나"에 답할 수 없고, 자체 평가가 무엇을 근거로 조정했는지도 사라진다.
/// 제안은 자기가 통과한 기준 버전을 들고 있어 나중에 그 시점의 잣대로 되짚을 수 있다.
/// </para>
/// </summary>
public sealed class ProposalStore
{
    private readonly ConcurrentDictionary<string, Proposal> _proposals = new(StringComparer.Ordinal);
    private readonly List<ProposalCriteria> _criteria = new();
    private readonly object _gate = new();
    private readonly IJournal<Proposal> _journal;
    private readonly IJournal<ProposalCriteria> _criteriaJournal;

    public ProposalStore(IJournalFactory? journals = null)
    {
        var factory = journals ?? NullJournalFactory.Instance;
        _journal = factory.Open<Proposal>("proposals");
        _criteriaJournal = factory.Open<ProposalCriteria>("proposal-criteria");

        foreach (var p in _journal.Load()) _proposals[p.Id] = p;      // 같은 id는 나중 것이 이긴다
        foreach (var c in _criteriaJournal.Load()) _criteria.Add(c);
        _criteria.Sort((a, b) => a.Version.CompareTo(b.Version));
    }

    /// <summary>현재 기준. 없으면 시드를 만들어 적재한다 — 기준 없는 상태는 존재하지 않는다.</summary>
    public ProposalCriteria Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_criteria.Count > 0) return _criteria[^1];
            var seed = ProposalCriteria.Seed(now);
            _criteria.Add(seed);
            _criteriaJournal.Append(seed);
            return seed;
        }
    }

    /// <summary>기준 개정. 버전은 저장소가 매긴다 — 호출자가 매기면 두 갱신이 같은 번호를 쓴다.</summary>
    public ProposalCriteria Revise(ProposalCriteria next)
    {
        lock (_gate)
        {
            var version = (_criteria.Count > 0 ? _criteria[^1].Version : 0) + 1;
            var stamped = next with { Version = version };
            _criteria.Add(stamped);
            _criteriaJournal.Append(stamped);
            return stamped;
        }
    }

    public IReadOnlyList<ProposalCriteria> CriteriaHistory()
    {
        lock (_gate) return _criteria.ToList();
    }

    public Proposal? Get(string id) => _proposals.GetValueOrDefault(id);

    /// <summary>
    /// 제안 적재. 같은 id가 이미 <b>결정돼</b> 있으면 덮지 않는다 —
    /// 덮으면 사람이 기각한 제안이 다음 생성에서 되살아나 영원히 다시 올라온다.
    /// </summary>
    public bool Put(Proposal proposal)
    {
        var existing = _proposals.GetValueOrDefault(proposal.Id);
        if (existing is not null && existing.Status != ProposalStatus.Proposed) return false;

        _proposals[proposal.Id] = proposal;
        _journal.Append(proposal);
        return true;
    }

    public Proposal? Decide(
        string id, string status, string actor, int? rating, string? note, DateTimeOffset at)
    {
        var existing = _proposals.GetValueOrDefault(id);
        if (existing is null || existing.Status != ProposalStatus.Proposed) return null;

        var decided = existing with
        {
            Status = status, DecidedAt = at, DecidedBy = actor, Rating = rating, DecisionNote = note,
        };
        _proposals[id] = decided;
        _journal.Append(decided);
        return decided;
    }

    /// <summary>최근 제안순. 결정된 것도 함께 — 대장이지 작업 목록이 아니다.</summary>
    public IReadOnlyList<Proposal> Snapshot(int limit = 200) =>
        _proposals.Values
            .OrderByDescending(p => p.ProposedAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

    public int Count => _proposals.Count;

    /// <summary>평가가 달린 제안만. 축 가중치 학습의 입력이다 — 축별 값과 평점이 함께 필요하다.</summary>
    public IReadOnlyList<Proposal> Rated() =>
        _proposals.Values.Where(p => p.Rating is not null).ToList();

    /// <summary>종류별 성적. 자체 평가가 기준을 조정할 때 보는 유일한 숫자다.</summary>
    public IReadOnlyList<KindOutcome> Outcomes() =>
        ProposalKind.All
            .Select(kind =>
            {
                var mine = _proposals.Values.Where(p => p.Kind == kind).ToList();
                var ratings = mine.Where(p => p.Rating is not null).Select(p => (double)p.Rating!.Value).ToList();
                return new KindOutcome(
                    kind,
                    Proposed: mine.Count,
                    Accepted: mine.Count(p => p.Status == ProposalStatus.Accepted),
                    Rejected: mine.Count(p => p.Status == ProposalStatus.Rejected),
                    Rated: ratings.Count,
                    MeanRating: ratings.Count == 0 ? null : Math.Round(ratings.Average(), 2));
            })
            .ToList();
}
