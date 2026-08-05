using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>
/// 완료 시점의 화면 상태 1건. <see cref="Observed"/>가 분모이고 <see cref="Filled"/>가 분자다 —
/// 화면에 없던 개념을 "안 채웠다"로 세면 그 필드가 없는 화면 변형 하나가
/// 규칙 전체를 흔든다.
/// </summary>
public sealed record CompletionRecord(
    string BusinessObject,
    string UserId,
    string Signature,
    IReadOnlyList<string> Observed,
    IReadOnlyList<string> Filled,
    DateTimeOffset At);

/// <summary>개념 1개의 완료 시점 채움 통계.</summary>
public sealed record ConceptFillStat(string Concept, int Observed, int Filled, int DistinctUsers)
{
    /// <summary>화면에 있었을 때 채워져 있던 비율. 필수 여부 판단의 유일한 증거다.</summary>
    public double FillRate => Observed == 0 ? 0 : (double)Filled / Observed;
}

/// <summary>업무객체 1종의 완료 관측 요약.</summary>
public sealed record CompletionStat(
    string BusinessObject,
    int Completions,
    int DistinctUsers,
    IReadOnlyList<ConceptFillStat> Concepts,
    DateTimeOffset LastSeen);

/// <summary>
/// 작업 완료 신호 저장소 (ARCHITECTURE §11).
///
/// <para>
/// 이것이 없으면 <c>rule.required.{BO}</c>는 영원히 <b>추측</b>이다. 시드가 "구매요청에는
/// 품목·수량·납기·거래처가 필요하다"고 말하지만 아무도 확인한 적이 없고, 가이드의
/// "…입력이 남았습니다" 제안은 전부 그 추측 위에 서 있다. 실제로 반복 거부되는 제안이
/// 도메인 축 신호로 잡히는 것도 같은 이유다 — 규칙이 틀렸을 수 있다는 증거다.
/// </para>
/// <para>
/// 작성 <b>중간</b>의 빈칸은 증거가 되지 않는다. 아직 안 채운 것인지 필요 없는 것인지
/// 구분되지 않기 때문이다. <b>저장을 누른 순간</b>의 화면만이 "이 업무객체가 실제로
/// 무엇을 요구했는가"를 말해 준다.
/// </para>
/// <para>값은 저장하지 않는다 — 개념 이름과 개수뿐이다.</para>
/// </summary>
public sealed class CompletionStore
{
    private readonly object _gate = new();
    private readonly List<CompletionRecord> _records = new();
    private readonly IJournal<CompletionRecord> _journal;

    public CompletionStore(IJournalFactory? journals = null)
    {
        _journal = (journals ?? NullJournalFactory.Instance).Open<CompletionRecord>("completions");
        _records.AddRange(_journal.Load());
    }

    public int Count { get { lock (_gate) return _records.Count; } }

    public void Record(CompletionRecord record)
    {
        if (record.Observed.Count == 0) return;   // 매핑이 없는 화면은 증거가 아니다

        lock (_gate)
        {
            _records.Add(record);
            _journal.Append(record);
        }
    }

    public IReadOnlyList<CompletionRecord> Snapshot(int limit = 100)
    {
        lock (_gate) return _records.AsEnumerable().Reverse().Take(limit).ToList();
    }

    public IReadOnlyList<CompletionStat> Stats()
    {
        lock (_gate)
        {
            return _records
                .GroupBy(r => r.BusinessObject, StringComparer.Ordinal)
                .Select(Fold)
                .OrderByDescending(s => s.Completions)
                .ThenBy(s => s.BusinessObject, StringComparer.Ordinal)
                .ToList();
        }
    }

    private static CompletionStat Fold(IGrouping<string, CompletionRecord> group)
    {
        var concepts = group
            .SelectMany(r => r.Observed.Select(c => (Concept: c, r.UserId, Filled: r.Filled.Contains(c))))
            .GroupBy(x => x.Concept, StringComparer.Ordinal)
            .Select(g => new ConceptFillStat(
                g.Key,
                Observed: g.Count(),
                Filled: g.Count(x => x.Filled),
                DistinctUsers: g.Select(x => x.UserId).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(c => c.FillRate)
            .ThenBy(c => c.Concept, StringComparer.Ordinal)
            .ToList();

        return new CompletionStat(
            group.Key,
            Completions: group.Count(),
            DistinctUsers: group.Select(r => r.UserId).Distinct(StringComparer.Ordinal).Count(),
            concepts,
            LastSeen: group.Max(r => r.At));
    }
}
