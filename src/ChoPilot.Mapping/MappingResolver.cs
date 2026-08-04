using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>
/// Adaptive Semantic Mapping 해석기 (ARCHITECTURE §5.2, PHASE1-DESIGN §4.2).
/// scope 캐스케이드(personal ▷ org ▷ global, D5) → 캐시 미스 시 AI 추론 → 캐시 적재.
/// </summary>
public sealed class MappingResolver
{
    /// <summary>해석 1건이 어디서 나왔는지. 캐시 적중률(H3b)과 AI 호출 수(H6 비용)를 갈라 세는 기준.</summary>
    public static class Source
    {
        /// <summary>θ_high를 넘는 캐시로 응답. 진짜 적중.</summary>
        public const string TrustedCache = "trusted_cache";

        /// <summary>저신뢰 캐시를 재사용하고 재추론은 보류. 적중은 아니지만 AI도 호출하지 않았다.</summary>
        public const string DeferredCache = "deferred_cache";

        /// <summary>AI를 실제로 호출했다.</summary>
        public const string Ai = "ai";
    }

    /// <summary>저신뢰 매핑을 다시 물어보기까지의 기본 대기 시간.</summary>
    public static readonly TimeSpan DefaultReinferAfter = TimeSpan.FromHours(24);

    private readonly IMappingCache _cache;
    private readonly IAiMapper _ai;
    private readonly double _thetaHigh;
    private readonly string _orgId;
    private readonly TimeSpan _reinferAfter;
    private readonly Func<DateTimeOffset> _clock;

    public MappingResolver(
        IMappingCache cache, IAiMapper ai, string orgId = "default", double thetaHigh = 0.8,
        TimeSpan? reinferAfter = null, Func<DateTimeOffset>? clock = null)
    {
        _cache = cache;
        _ai = ai;
        _orgId = orgId;
        _thetaHigh = thetaHigh;
        _reinferAfter = reinferAfter ?? DefaultReinferAfter;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>토큰 사용량은 AI를 호출하지 않았을 때 null이다 — H6 비용 집계의 기준.</summary>
    public sealed record ResolveResult(
        MappingEntry Entry, string Source, int? InputTokens = null, int? OutputTokens = null)
    {
        public bool CacheHit => Source == MappingResolver.Source.TrustedCache;

        /// <summary>
        /// AI를 실제로 호출했는지. <see cref="CacheHit"/>의 부정이 <b>아니다</b> —
        /// 저신뢰 캐시 재사용은 적중도 아니고 호출도 아니다. 둘을 같게 보면 AI 호출 수가 부풀려진다.
        /// </summary>
        public bool AiCalled => Source == MappingResolver.Source.Ai;
    }

    public async Task<ResolveResult> ResolveAsync(
        string signature, string userId, ScreenInfo screen, UiNode tree,
        Concept[] ontology, string businessHint, CancellationToken ct = default)
    {
        MappingEntry? lowConfidence = null;

        foreach (var scope in new[] { $"personal:{userId}", $"org:{_orgId}", "global" })
        {
            var cached = _cache.Get(signature, scope);
            if (cached is null) continue;

            if (cached.Confidence >= _thetaHigh)
                return new ResolveResult(cached, Source.TrustedCache);

            lowConfidence ??= cached;   // 캐스케이드 순서상 첫 저신뢰 후보 — 쓸 수는 있다
        }

        // ── θ 절벽 ────────────────────────────────────────────────────────────
        // 저신뢰 매핑은 자기 신뢰도로 적중 조건을 만족시킬 수 없다. 그대로 두면 같은 화면을
        // 볼 때마다 영원히 Bedrock을 다시 호출한다(실측: θ=0.8에서 동일 화면 20회 관측 → 20회 호출,
        // θ=0.5에서는 1회). 예열 문제가 아니라 영구적인 상태다.
        //
        // 같은 트리·같은 온톨로지·같은 모델에 다시 물으면 대체로 같은 답이 온다 → 즉시 재추론은 낭비다.
        // 재추론이 의미 있는 건 온톨로지나 모델이 바뀐 뒤이고, 그건 배포 단위 사건이지 관측 단위가 아니다.
        // 그래서 시간 백오프를 둔다. 신뢰도를 실제로 끌어올리는 길은 재추론이 아니라
        // 사람의 검수·보정이다(/v1/review, /v1/correction — 보정 후 호출량은 0으로 떨어진다).
        var now = _clock();
        var target = _cache.Get(signature, "global");   // AI 결과가 덮어쓸 자리
        if (lowConfidence is not null &&
            target?.LastInferredAt is { } lastInferred &&
            now - lastInferred < _reinferAfter)
        {
            return new ResolveResult(lowConfidence, Source.DeferredCache);
        }

        // 캐시 미스 / 백오프 만료 → AI 동적 매핑
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
            Status: confidence >= _thetaHigh ? "trusted" : "pending_review",
            LastInferredAt: now);

        _cache.Put(entry);
        return new ResolveResult(entry, Source.Ai, inference.InputTokens, inference.OutputTokens);
    }
}
