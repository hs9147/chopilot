using ChoPilot.Core;
using System.Collections.Concurrent;

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
    private readonly ConcurrentDictionary<string, Lazy<Task<ResolveResult>>> _inflight = new(StringComparer.Ordinal);

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
        CompiledKnowledge knowledge, string businessHint, CancellationToken ct = default)
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
        // 재추론이 의미 있는 건 온톨로지나 모델이 바뀐 뒤다 — 그래서 백오프는 시간과 함께
        // 지식 버전을 본다: 추론 당시 버전과 현재 버전이 다르면 백오프를 무시하고 다시 묻는다
        // (개념이 추가·폐기됐으니 같은 트리라도 답이 달라질 수 있다).
        // 신뢰도를 실제로 끌어올리는 길은 여전히 사람의 검수·보정이다(/v1/review, /v1/correction).
        var now = _clock();
        var target = _cache.Get(signature, "global");   // AI 결과가 덮어쓸 자리
        if (lowConfidence is not null &&
            target?.LastInferredAt is { } lastInferred &&
            target.OntologyVersion == knowledge.Version &&
            now - lastInferred < _reinferAfter)
        {
            return new ResolveResult(lowConfidence, Source.DeferredCache);
        }

        // 캐시 미스 / 백오프 만료 / 지식 버전 변경 → 같은 구조·지식 버전은 한 번만 AI 호출.
        var flightKey = $"{signature}\u001f{knowledge.Version}";
        var lazy = _inflight.GetOrAdd(flightKey, _ => new Lazy<Task<ResolveResult>>(
            // 첫 HTTP 요청이 취소돼도 같은 추론을 기다리던 다른 요청까지 취소하면 안 된다.
            // 호출자 취소는 아래 WaitAsync에서만 적용하고, 공유 작업은 캐시를 채울 때까지 완주한다.
            () => InferAndCacheAsync(signature, screen, tree, knowledge, businessHint, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        _ = task.ContinueWith(_ =>
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<ResolveResult>>>(flightKey, lazy)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(ct);
    }

    private async Task<ResolveResult> InferAndCacheAsync(
        string signature, ScreenInfo screen, UiNode tree,
        CompiledKnowledge knowledge, string businessHint, CancellationToken ct)
    {
        // 기다리는 동안 앞 요청이 캐시를 채웠을 수 있다.
        if (_cache.Get(signature, "global") is { } filled &&
            filled.Confidence >= _thetaHigh &&
            filled.OntologyVersion == knowledge.Version)
            return new ResolveResult(filled, Source.TrustedCache);

        var inference = await _ai.InferAsync(businessHint, tree, knowledge.Concepts, ct);
        var validRefs = Flatten(tree).Select(n => n.Ref).ToHashSet(StringComparer.Ordinal);
        var fields = inference.Fields
            .Where(f => validRefs.Contains(f.ElementRef))
            .GroupBy(f => f.ElementRef, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(f => f.Confidence).First())
            .Select(f => f with { Confidence = Math.Clamp(f.Confidence, 0, 1) })
            .ToList();

        var confidence = fields.Count == 0
            ? 0
            : fields.Average(f => f.Confidence);

        var businessObject = string.IsNullOrWhiteSpace(inference.BusinessObject) ||
                             inference.BusinessObject.Length > 128
            ? businessHint
            : inference.BusinessObject.Trim();

        var entry = new MappingEntry(
            Signature: signature,
            Scope: "global",                          // 신규 구조 지식은 Shared Plane에 적재
            UserId: null,
            BusinessObject: businessObject,
            RecordId: screen.RecordHint,
            Mapping: fields,
            Confidence: confidence,
            Status: confidence >= _thetaHigh ? "trusted" : "pending_review",
            LastInferredAt: _clock(),
            OntologyVersion: knowledge.Version);

        _cache.Put(entry);
        return new ResolveResult(entry, Source.Ai, inference.InputTokens, inference.OutputTokens);
    }

    private static IEnumerable<UiNode> Flatten(UiNode root)
    {
        var stack = new Stack<UiNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children) stack.Push(child);
        }
    }
}
