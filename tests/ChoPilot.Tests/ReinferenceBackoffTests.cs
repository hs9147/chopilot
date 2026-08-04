using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Xunit;

namespace ChoPilot.Tests;

/// <summary>
/// θ 절벽: 저신뢰 매핑은 자기 신뢰도로 적중 조건을 만족시킬 수 없어, 백오프가 없으면
/// 같은 화면을 볼 때마다 영원히 AI를 다시 호출한다(실측 θ=0.8, 동일 화면 20회 → 20회 호출).
/// </summary>
public class ReinferenceBackoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class CountingMapper : IAiMapper
    {
        public int Calls;
        public double Confidence = 0.5;               // θ_high(0.8) 미만 — 절벽에 걸린다

        public Task<MappingInference> InferAsync(
            string hint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new MappingInference("PurchaseRequest",
                new() { new FieldMapping("n2", "Material", Confidence, "ai", false) }));
        }
    }

    private static readonly ScreenInfo Screen = new("https://proc/pr/create", "구매요청", null);
    private static readonly UiNode Tree = new("n1", "Form", null, null, null, new()
    {
        new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
    });

    private static string Signature => SignatureService.Compute(Screen, Tree);

    private static Task<MappingResolver.ResolveResult> Resolve(MappingResolver resolver, string user = "u1") =>
        resolver.ResolveAsync(Signature, user, Screen, Tree, ProcurementOntology.Concepts, "PurchaseRequest");

    [Fact]
    public async Task LowConfidence_IsInferredOnce_ThenReused()
    {
        var mapper = new CountingMapper();
        var now = T0;
        var resolver = new MappingResolver(new InMemoryMappingCache(), mapper,
            thetaHigh: 0.8, reinferAfter: TimeSpan.FromHours(24), clock: () => now);

        var first = await Resolve(resolver);
        Assert.Equal(MappingResolver.Source.Ai, first.Source);
        Assert.Equal("pending_review", first.Entry.Status);

        for (var i = 0; i < 19; i++)
        {
            now = now.AddMinutes(1);
            var again = await Resolve(resolver);

            Assert.Equal(MappingResolver.Source.DeferredCache, again.Source);
            Assert.False(again.CacheHit);             // 적중은 아니다 — H3b를 부풀리지 않는다
            Assert.False(again.AiCalled);
            Assert.Null(again.InputTokens);           // 호출하지 않았으니 토큰도 없다
            Assert.Equal("Material", again.Entry.Mapping[0].Concept);   // 그래도 쓸 수 있는 답은 준다
        }

        Assert.Equal(1, mapper.Calls);                // 백오프 없으면 20
    }

    [Fact]
    public async Task Backoff_Expires_AndTheQuestionIsAskedAgain()
    {
        var mapper = new CountingMapper();
        var now = T0;
        var resolver = new MappingResolver(new InMemoryMappingCache(), mapper,
            thetaHigh: 0.8, reinferAfter: TimeSpan.FromHours(24), clock: () => now);

        await Resolve(resolver);

        now = T0.AddHours(23);
        Assert.Equal(MappingResolver.Source.DeferredCache, (await Resolve(resolver)).Source);

        // 온톨로지·모델이 바뀌었을 수 있다 → 하루에 한 번은 다시 물어본다
        now = T0.AddHours(25);
        Assert.Equal(MappingResolver.Source.Ai, (await Resolve(resolver)).Source);
        Assert.Equal(2, mapper.Calls);
    }

    [Fact]
    public async Task Reinference_ThatSucceeds_RestoresTrustedHits()
    {
        var mapper = new CountingMapper();
        var now = T0;
        var resolver = new MappingResolver(new InMemoryMappingCache(), mapper,
            thetaHigh: 0.8, reinferAfter: TimeSpan.FromHours(24), clock: () => now);

        await Resolve(resolver);

        mapper.Confidence = 0.95;                     // 모델이 좋아졌다
        now = T0.AddHours(25);
        Assert.Equal(MappingResolver.Source.Ai, (await Resolve(resolver)).Source);

        now = now.AddMinutes(1);
        var hit = await Resolve(resolver);
        Assert.Equal(MappingResolver.Source.TrustedCache, hit.Source);
        Assert.True(hit.CacheHit);
        Assert.Equal(2, mapper.Calls);                // 이후로는 절대 호출하지 않는다
    }

    [Fact]
    public async Task Correction_EndsTheDeferral_Entirely()
    {
        var cache = new InMemoryMappingCache();
        var mapper = new CountingMapper();
        var now = T0;
        var resolver = new MappingResolver(cache, mapper,
            thetaHigh: 0.8, reinferAfter: TimeSpan.FromHours(24), clock: () => now);

        await Resolve(resolver);
        Assert.Equal(1, mapper.Calls);

        new PersonalizationService(cache).ApplyCorrection("u1", new CorrectionRequest(
            Signature, "PurchaseRequest", new() { new CorrectionField("n2", "Material") }));

        // 백오프는 호출을 미루기만 한다. 신뢰도를 실제로 올리는 건 사람의 보정이다.
        for (var i = 0; i < 5; i++)
        {
            now = now.AddHours(48);                   // 백오프가 만료돼도
            var res = await Resolve(resolver);
            Assert.Equal(MappingResolver.Source.TrustedCache, res.Source);
            Assert.Equal("personal:u1", res.Entry.Scope);
        }

        Assert.Equal(1, mapper.Calls);                // 다시 묻지 않는다
    }

    [Fact]
    public async Task Promotion_EndsTheDeferral_ForEveryone()
    {
        var cache = new InMemoryMappingCache();
        var mapper = new CountingMapper();
        var now = T0;
        var resolver = new MappingResolver(cache, mapper,
            thetaHigh: 0.8, reinferAfter: TimeSpan.FromHours(24), clock: () => now);

        await Resolve(resolver, "alice");
        new PersonalizationService(cache).Promote(new PromoteRequest(Signature, "global", Confidence: 0.9));

        now = now.AddHours(48);
        var bob = await Resolve(resolver, "bob");     // 검수는 공용 평면을 고친다
        Assert.Equal(MappingResolver.Source.TrustedCache, bob.Source);
        Assert.Equal(1, mapper.Calls);
    }

    [Fact]
    public async Task ColdCache_IsNotDeferred()
    {
        var mapper = new CountingMapper();
        var resolver = new MappingResolver(new InMemoryMappingCache(), mapper,
            thetaHigh: 0.8, reinferAfter: TimeSpan.FromHours(24), clock: () => T0);

        // 아직 아무것도 모르는 화면은 미루면 안 된다 — 미룰 답이 없다
        Assert.Equal(MappingResolver.Source.Ai, (await Resolve(resolver)).Source);
    }
}
