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
    string Status,
    double DurationMs,
    int? InputTokens,
    int? OutputTokens,
    string Source = MappingResolver.Source.Ai,
    string TenantId = "default");   // trusted_cache | deferred_cache | ai

/// <summary>
/// H3b(캐시 적중률)·H6(지연 p95, AI 토큰비용) 집계 (PHASE0-KIT §3.4·§3.5, PHASE1-DESIGN §7).
/// 측정표를 손으로 채우는 대신 감사 로그에서 직접 산출한다.
/// </summary>
public sealed record MetricsSnapshot(
    int Observations,
    int CacheHits,
    int CacheMisses,
    double CacheHitRatio,
    int DistinctSignatures,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyMaxMs,
    int AiCalls,
    int DeferredReuses,
    int InputTokens,
    int OutputTokens,
    int MaskedRefs,
    Dictionary<string, int> ByProvenance,
    Dictionary<string, int> ByStatus);

/// <summary>
/// 관측·판단 이력을 <b>추가 전용(append-only)</b>으로 남긴다 (PHASE1-DESIGN §2.2 AuditService).
/// 수정·삭제 API를 제공하지 않는다 → 불변성. 운영은 CloudTrail + 감사테이블(ARCHITECTURE §8).
/// </summary>
public sealed class AuditService
{
    private readonly ConcurrentQueue<AuditEntry> _log = new();
    private readonly object _gate = new();
    private readonly IJournal<AuditEntry> _journal;
    private long _seq;

    public AuditService(IJournalFactory? journals = null)
    {
        _journal = (journals ?? NullJournalFactory.Instance).Open<AuditEntry>("audit");
        foreach (var entry in _journal.Load())
        {
            _log.Enqueue(entry);
            // 재시작 후 Seq가 다시 1부터 시작하면 감사 로그의 순서가 뒤엉킨다.
            if (entry.Seq > _seq) _seq = entry.Seq;
        }
    }

    /// <summary>복원 중 건너뛴 손상 줄 수 (부팅 로그용).</summary>
    public int CorruptOnLoad => _journal.Corrupt;

    public AuditEntry Record(
        ObservationEvent evt, string signature, MappingResolver.ResolveResult result, double durationMs,
        string tenantId = "default")
    {
        lock (_gate)
        {
            var entry = result.Entry;
            var e = new AuditEntry(
                Seq: Interlocked.Increment(ref _seq),
                At: DateTimeOffset.UtcNow,
                EventId: evt.EventId,
                UserId: evt.UserId,
                SessionId: evt.SessionId,
                Signature: signature,
                BusinessObject: entry.BusinessObject,
                Confidence: entry.Confidence,
                CacheHit: result.CacheHit,
                Provenance: entry.Mapping.FirstOrDefault()?.Provenance ?? "cache",
                MaskedRefCount: evt.Privacy.MaskedRefs.Count,
                Status: entry.Status,
                DurationMs: durationMs,
                InputTokens: result.InputTokens,
                OutputTokens: result.OutputTokens,
                Source: result.Source,
                TenantId: tenantId);
            _journal.Append(e);
            _log.Enqueue(e);
            return e;
        }
    }

    /// <summary>감사 로그 스냅샷(읽기 전용). 최신순.</summary>
    public IReadOnlyList<AuditEntry> Snapshot(int limit = 100) =>
        _log.Reverse().Take(limit).ToList();

    public int Count => _log.Count;

    /// <summary>이벤트별 최종 캐시 적중 여부 (측정 UI 요약용). 같은 ID가 재전송되면 마지막 판정을 쓴다.</summary>
    public IReadOnlyDictionary<string, bool> CacheHitByEventId() =>
        _log.GroupBy(e => e.EventId).ToDictionary(g => g.Key, g => g.Last().CacheHit);

    /// <summary>전체 감사 로그에서 H3b·H6 지표를 산출.</summary>
    public MetricsSnapshot Metrics()
    {
        var entries = _log.ToArray();
        if (entries.Length == 0)
            return new MetricsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new(), new());

        var hits = entries.Count(e => e.CacheHit);
        var durations = entries.Select(e => e.DurationMs).OrderBy(d => d).ToArray();

        return new MetricsSnapshot(
            Observations: entries.Length,
            CacheHits: hits,
            CacheMisses: entries.Length - hits,
            CacheHitRatio: Math.Round((double)hits / entries.Length, 4),
            DistinctSignatures: entries.Select(e => e.Signature).Distinct().Count(),
            LatencyP50Ms: Math.Round(Percentile(durations, 0.50), 2),
            LatencyP95Ms: Math.Round(Percentile(durations, 0.95), 2),
            LatencyMaxMs: Math.Round(durations[^1], 2),
            // 미스 = AI 호출이 아니다. 저신뢰 캐시를 재사용한 관측은 적중도 호출도 아니라,
            // 미스에서 빼서 세지 않으면 AI 호출 수(=비용)가 부풀려진다.
            AiCalls: entries.Count(e => e.Source == MappingResolver.Source.Ai),
            DeferredReuses: entries.Count(e => e.Source == MappingResolver.Source.DeferredCache),
            InputTokens: entries.Sum(e => e.InputTokens ?? 0),
            OutputTokens: entries.Sum(e => e.OutputTokens ?? 0),
            MaskedRefs: entries.Sum(e => e.MaskedRefCount),
            ByProvenance: entries.GroupBy(e => e.Provenance).ToDictionary(g => g.Key, g => g.Count()),
            ByStatus: entries.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>정렬된 표본의 최근접 순위(nearest-rank) 백분위수.</summary>
    private static double Percentile(double[] sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

}
