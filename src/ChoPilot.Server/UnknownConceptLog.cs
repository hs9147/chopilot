using System.Collections.Concurrent;
using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>온톨로지에 없는 개념으로 정정을 시도한 사실 1건.</summary>
public sealed record UnknownConceptAttempt(
    string Term,          // 사용자가 입력한 그대로 ("결제조건")
    string UserId,
    string Signature,
    string BusinessObject,
    DateTimeOffset At);

/// <summary>개념 후보 1건 — 시도들을 용어별로 접은 것.</summary>
public sealed record UnknownConceptCandidate(
    string Term,
    int Attempts,
    int DistinctUsers,
    IReadOnlyList<string> BusinessObjects,
    DateTimeOffset LastSeen);

/// <summary>
/// 보정에서 <b>거부된</b> 개념 시도 기록 (ARCHITECTURE §5.5 1단계 Signal).
///
/// <para>
/// 거부는 옳지만 거부만으로는 아무것도 자라지 않는다 — 사용자가 "결제조건"으로 다섯 번
/// 정정을 시도했다는 사실은 <b>그 개념이 온톨로지에 빠져 있다는 가장 강한 증거</b>이고,
/// 지금까지 그 증거는 400 응답과 함께 버려지고 있었다. 제안 거부를 신호로 남긴 것과 같은 논리다.
/// </para>
/// <para>
/// 용어는 사용자가 입력한 <b>라벨</b>이지 관측된 값이 아니다 — 화면에 보이는 필드 이름이므로
/// 구조 정보에 해당한다. 그럼에도 org 승격에는 k인 게이트가 걸린다(<see cref="Candidates"/>).
/// </para>
/// </summary>
public sealed class UnknownConceptLog
{
    private readonly ConcurrentQueue<UnknownConceptAttempt> _attempts = new();
    private readonly IJournal<UnknownConceptAttempt> _journal;

    public UnknownConceptLog(IJournalFactory? journals = null)
    {
        _journal = (journals ?? NullJournalFactory.Instance).Open<UnknownConceptAttempt>("unknown-concepts");
        foreach (var attempt in _journal.Load()) _attempts.Enqueue(attempt);
    }

    public void Record(string userId, string signature, string businessObject,
                       IEnumerable<string> terms, DateTimeOffset at)
    {
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            var attempt = new UnknownConceptAttempt(term.Trim(), userId, signature, businessObject, at);
            _attempts.Enqueue(attempt);
            _journal.Append(attempt);
        }
    }

    public int Count => _attempts.Count;

    public IReadOnlyList<UnknownConceptAttempt> Snapshot(int limit = 200) =>
        _attempts.Reverse().Take(limit).ToList();

    /// <summary>
    /// 용어별 집계. 대소문자만 다른 입력은 같은 후보로 접는다.
    /// 시도 횟수 내림차순 — 자주 부딪히는 결핍이 먼저 온다.
    /// </summary>
    public IReadOnlyList<UnknownConceptCandidate> Candidates() =>
        _attempts.ToArray()
            .GroupBy(a => a.Term, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UnknownConceptCandidate(
                Term: g.First().Term,
                Attempts: g.Count(),
                DistinctUsers: g.Select(a => a.UserId).Distinct(StringComparer.Ordinal).Count(),
                BusinessObjects: g.Select(a => a.BusinessObject).Distinct(StringComparer.Ordinal)
                                  .OrderBy(b => b, StringComparer.Ordinal).ToList(),
                LastSeen: g.Max(a => a.At)))
            .OrderByDescending(c => c.Attempts)
            .ThenBy(c => c.Term, StringComparer.Ordinal)
            .ToList();
}
