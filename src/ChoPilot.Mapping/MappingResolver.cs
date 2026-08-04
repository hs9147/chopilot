using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>
/// Adaptive Semantic Mapping 해석기 (ARCHITECTURE §5.2, PHASE1-DESIGN §4.2).
/// scope 캐스케이드(personal ▷ org ▷ global, D5) → 캐시 미스 시 AI 추론 → 캐시 적재.
/// </summary>
public sealed class MappingResolver
{
    private readonly IMappingCache _cache;
    private readonly IAiMapper _ai;
    private readonly double _thetaHigh;
    private readonly string _orgId;

    public MappingResolver(IMappingCache cache, IAiMapper ai, string orgId = "default", double thetaHigh = 0.8)
    {
        _cache = cache;
        _ai = ai;
        _orgId = orgId;
        _thetaHigh = thetaHigh;
    }

    /// <summary>토큰 사용량은 캐시 HIT(=AI 미호출)일 때 null이다 — H6 비용 집계의 기준.</summary>
    public sealed record ResolveResult(
        MappingEntry Entry, bool CacheHit, int? InputTokens = null, int? OutputTokens = null);

    public async Task<ResolveResult> ResolveAsync(
        string signature, string userId, ScreenInfo screen, UiNode tree,
        Concept[] ontology, string businessHint, CancellationToken ct = default)
    {
        foreach (var scope in new[] { $"personal:{userId}", $"org:{_orgId}", "global" })
        {
            var cached = _cache.Get(signature, scope);
            if (cached is not null && cached.Confidence >= _thetaHigh)
                return new ResolveResult(cached, CacheHit: true);
        }

        // 캐시 미스/저신뢰 → AI 동적 매핑
        var inference = await _ai.InferAsync(businessHint, tree, ontology, ct);
        var confidence = inference.Fields.Count == 0
            ? 0
            : inference.Fields.Average(f => f.Confidence);

        var entry = new MappingEntry(
            Signature: signature,
            Scope: "global",                          // 신규 구조 지식은 Shared Plane에 적재
            UserId: null,
            BusinessObject: inference.BusinessObject,
            RecordId: screen.RecordHint,
            Mapping: inference.Fields,
            Confidence: confidence,
            Status: confidence >= _thetaHigh ? "trusted" : "pending_review");

        _cache.Put(entry);
        return new ResolveResult(entry, CacheHit: false, inference.InputTokens, inference.OutputTokens);
    }
}
