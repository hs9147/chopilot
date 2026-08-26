using System.Collections.Concurrent;
using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>
/// 사람이 내린 결정 1건 (HITL). 관측 기반의 <see cref="AuditEntry"/>와 형태가 달라 따로 남긴다.
/// </summary>
public sealed record DecisionEntry(
    long Seq,
    DateTimeOffset At,
    string Action,        // "promote" | "correction"
    string Actor,         // 결정한 사람 (RequestUser)
    string Signature,
    string Scope,
    double Confidence,
    string Detail);

/// <summary>
/// 검수·보정 결정의 <b>추가 전용</b> 이력 (ARCHITECTURE §8 "전 관측 Audit", §5.2 step 6 HITL).
///
/// <para>
/// 승격과 개인 보정은 매핑 캐시를 영구히 바꾸는 판단이다. 특히 승격은 신뢰도를 임의 값으로
/// 덮어쓸 수 있어, 누가 언제 무엇을 승인했는지가 남지 않으면 잘못된 매핑의 출처를 되짚을 수 없다.
/// </para>
/// </summary>
public sealed class DecisionLog
{
    private readonly ConcurrentQueue<DecisionEntry> _log = new();
    private readonly IJournal<DecisionEntry> _journal;
    private long _seq;

    public DecisionLog(IJournalFactory? journals = null)
    {
        _journal = (journals ?? NullJournalFactory.Instance).Open<DecisionEntry>("decisions");
        foreach (var entry in _journal.Load())
        {
            _log.Enqueue(entry);
            if (entry.Seq > _seq) _seq = entry.Seq;
        }
    }

    public DecisionEntry Record(string action, string actor, string signature, string scope, double confidence, string detail)
    {
        var entry = new DecisionEntry(
            Seq: Interlocked.Increment(ref _seq),
            At: DateTimeOffset.UtcNow,
            Action: action,
            Actor: actor,
            Signature: signature,
            Scope: scope,
            Confidence: confidence,
            Detail: detail);

        _log.Enqueue(entry);
        _journal.Append(entry);
        return entry;
    }

    /// <summary>결정 이력 스냅샷(읽기 전용). 최신순.</summary>
    public IReadOnlyList<DecisionEntry> Snapshot(int limit = 100) =>
        _log.Reverse().Take(limit).ToList();

    public int Count => _log.Count;
}
