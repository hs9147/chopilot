using System.Collections.Concurrent;
using ChoPilot.Core;
using ChoPilot.Mapping;

namespace ChoPilot.Server;

/// <summary>불변 감사 레코드 (ARCHITECTURE §7·§8, PHASE1-DESIGN §2.2). 관측→판단 1건.</summary>
public sealed record AuditEntry(
    long Seq,
    DateTimeOffset At,
    string EventId,
    string UserId,
    string SessionId,
    string Signature,
    string BusinessObject,
    double Confidence,
    bool CacheHit,
    string Provenance,
    int MaskedRefCount,
    string Status);

/// <summary>
/// 관측·판단 이력을 <b>추가 전용(append-only)</b>으로 남긴다 (PHASE1-DESIGN §2.2 AuditService).
/// 수정·삭제 API를 제공하지 않는다 → 불변성. 운영은 CloudTrail + 감사테이블(ARCHITECTURE §8).
/// </summary>
public sealed class AuditService
{
    private readonly ConcurrentQueue<AuditEntry> _log = new();
    private long _seq;

    public AuditEntry Record(
        ObservationEvent evt, string signature, MappingEntry entry, bool cacheHit)
    {
        var e = new AuditEntry(
            Seq: Interlocked.Increment(ref _seq),
            At: DateTimeOffset.UtcNow,
            EventId: evt.EventId,
            UserId: evt.UserId,
            SessionId: evt.SessionId,
            Signature: signature,
            BusinessObject: entry.BusinessObject,
            Confidence: entry.Confidence,
            CacheHit: cacheHit,
            Provenance: entry.Mapping.FirstOrDefault()?.Provenance ?? "cache",
            MaskedRefCount: evt.Privacy.MaskedRefs.Count,
            Status: entry.Status);
        _log.Enqueue(e);
        return e;
    }

    /// <summary>감사 로그 스냅샷(읽기 전용). 최신순.</summary>
    public IReadOnlyList<AuditEntry> Snapshot(int limit = 100) =>
        _log.Reverse().Take(limit).ToList();

    public int Count => _log.Count;
}
