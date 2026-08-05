using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

public class OntologyResolveTests
{
    [Fact]
    public void Resolve_Matches_ConceptName_And_Alias()
    {
        Assert.Equal("UnitPrice", ProcurementOntology.Resolve("UnitPrice")!.Name);
        Assert.Equal("UnitPrice", ProcurementOntology.Resolve("단가")!.Name);      // 화면 라벨
        Assert.Equal("UnitPrice", ProcurementOntology.Resolve(" unit price ")!.Name);
    }

    [Fact]
    public void Resolve_ReturnsNull_ForUnknown()
    {
        Assert.Null(ProcurementOntology.Resolve("NotAConcept"));
        Assert.Null(ProcurementOntology.Resolve(""));
        Assert.Null(ProcurementOntology.Resolve(null));
    }

    [Fact]
    public void ByName_StillIgnores_Aliases()
    {
        // Resolve와 역할을 나눠 둔다: ByName은 정규 이름만, Resolve는 사람 입력용.
        Assert.NotNull(ProcurementOntology.ByName("UnitPrice"));
        Assert.Null(ProcurementOntology.ByName("단가"));
    }
}

public class UepStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordVisit_Accumulates_Count_And_Recency()
    {
        var uep = new UepStore();
        uep.RecordVisit("u1", "sigA", T0);
        uep.RecordVisit("u1", "sigA", T0.AddMinutes(5));
        uep.RecordVisit("u1", "sigB", T0.AddMinutes(1));

        var profile = uep.Get("u1")!;
        Assert.Equal("u1", profile.UserId);
        Assert.Equal(2, profile.Screens.Count);

        var a = profile.Screens.First(s => s.Signature == "sigA");
        Assert.Equal(2, a.Count);
        Assert.Equal(T0, a.FirstSeen);                 // 최초 보존
        Assert.Equal(T0.AddMinutes(5), a.LastSeen);    // 최근 갱신
    }

    [Fact]
    public void Get_Orders_ByFrequency_Descending()
    {
        var uep = new UepStore();
        uep.RecordVisit("u1", "rare", T0);
        uep.RecordVisit("u1", "often", T0);
        uep.RecordVisit("u1", "often", T0.AddMinutes(1));

        Assert.Equal("often", uep.Get("u1")!.Screens[0].Signature);   // 자주 쓰는 화면 우선
    }

    [Fact]
    public void Profiles_AreIsolated_PerUser()
    {
        var uep = new UepStore();
        uep.RecordVisit("u1", "sigA", T0);

        Assert.NotNull(uep.Get("u1"));
        Assert.Null(uep.Get("u2"));                     // 사용자 간 격리(D5)
    }
}

public class PersonalizationServiceTests
{
    private static MappingEntry Pending(string sig, double conf, string scope = "global") => new(
        sig, scope, null, "PurchaseRequest", null,
        new() { new FieldMapping("n2", "Material", conf, "ai", false) },
        conf, "pending_review");

    [Fact]
    public void ReviewQueue_Returns_OnlyPending_LowestConfidenceFirst()
    {
        var cache = new InMemoryMappingCache();
        cache.Put(Pending("sig-low", 0.4));
        cache.Put(Pending("sig-mid", 0.7));
        cache.Put(Pending("sig-hi", 0.75) with { Status = "trusted" });   // trusted 제외
        var svc = new PersonalizationService(cache, new KnowledgeStore());

        var queue = svc.ReviewQueue();

        Assert.Equal(2, queue.Count);
        Assert.Equal("sig-low", queue[0].Signature);    // 낮은 신뢰도부터
    }

    [Fact]
    public void ReviewQueue_Excludes_PersonalScope()
    {
        // 검수 큐는 여러 사람이 함께 보는 화면 — 개인 매핑이 실리면 격리가 깨진다.
        var cache = new InMemoryMappingCache();
        cache.Put(Pending("sig-shared", 0.5));
        cache.Put(Pending("sig-mine", 0.3, scope: "personal:alice"));
        var svc = new PersonalizationService(cache, new KnowledgeStore());

        var queue = svc.ReviewQueue();

        Assert.Single(queue);
        Assert.Equal("sig-shared", queue[0].Signature);
    }

    [Fact]
    public void Promote_FlipsStatus_ToTrusted()
    {
        var cache = new InMemoryMappingCache();
        cache.Put(Pending("sig1", 0.5));
        var svc = new PersonalizationService(cache, new KnowledgeStore());

        var promoted = svc.Promote(new PromoteRequest("sig1", "global", Confidence: 0.9));

        Assert.NotNull(promoted);
        Assert.Equal("trusted", promoted!.Status);
        Assert.Equal(0.9, promoted.Confidence);
        Assert.Equal("trusted", cache.Get("sig1", "global")!.Status);
    }

    [Fact]
    public void Promote_ReturnsNull_ForUnknown() =>
        Assert.Null(new PersonalizationService(new InMemoryMappingCache(), new KnowledgeStore())
            .Promote(new PromoteRequest("nope", "global")));

    [Fact]
    public void ApplyCorrection_Writes_PersonalScope_HighConfidence()
    {
        var cache = new InMemoryMappingCache();
        var svc = new PersonalizationService(cache, new KnowledgeStore());

        var outcome = svc.ApplyCorrection("user7", new CorrectionRequest(
            "sigX", "PurchaseRequest", new() { new CorrectionField("n2", "UnitPrice") }));

        Assert.True(outcome.Accepted);
        var entry = outcome.Entry!;
        Assert.Equal("personal:user7", entry.Scope);
        Assert.Equal(1.0, entry.Confidence);
        Assert.Equal("user", entry.Mapping[0].Provenance);
        Assert.True(entry.Mapping[0].Sensitive);         // 온톨로지 sensitive 반영
        Assert.NotNull(cache.Get("sigX", "personal:user7"));
    }

    [Fact]
    public void ApplyCorrection_Accepts_AliasConcept_AndKeepsSensitiveFlag()
    {
        // 사용자는 화면에 보이는 "단가"로 정정한다. 별칭을 못 읽으면 민감 플래그가 꺼져버린다.
        var svc = new PersonalizationService(new InMemoryMappingCache(), new KnowledgeStore());

        var outcome = svc.ApplyCorrection("u1", new CorrectionRequest(
            "sigX", "PurchaseRequest", new() { new CorrectionField("n2", "단가") }));

        Assert.True(outcome.Accepted);
        Assert.Equal("UnitPrice", outcome.Entry!.Mapping[0].Concept);   // 정규 개념명으로 저장
        Assert.True(outcome.Entry.Mapping[0].Sensitive);
    }

    [Fact]
    public void ApplyCorrection_Rejects_UnknownConcept_Entirely()
    {
        var cache = new InMemoryMappingCache();
        var svc = new PersonalizationService(cache, new KnowledgeStore());

        var outcome = svc.ApplyCorrection("u1", new CorrectionRequest(
            "sigX", "PurchaseRequest", new()
            {
                new CorrectionField("n2", "Material"),      // 유효
                new CorrectionField("n3", "가격정보"),        // 온톨로지에 없다
            }));

        Assert.False(outcome.Accepted);
        Assert.Equal(new[] { "가격정보" }, outcome.UnknownConcepts);
        Assert.Null(cache.Get("sigX", "personal:u1"));      // 일부만 적재되지 않는다
    }

    [Fact]
    public void AliasCorrection_StillSuppresses_SensitiveValue_InBusinessObject()
    {
        // 결함의 실제 결과까지 고정한다: 별칭 정정이 BO의 민감값 억제를 무력화하면 안 된다.
        var svc = new PersonalizationService(new InMemoryMappingCache(), new KnowledgeStore());
        var outcome = svc.ApplyCorrection("u1", new CorrectionRequest(
            "sigX", "PurchaseRequest", new() { new CorrectionField("n2", "단가") }));

        var tree = new UiNode("n1", "Form", null, null, null, new()
        {
            new("n2", "Edit", null, "12000", "txtPrice", new()),   // 클라이언트 마스킹이 놓친 원문
        });

        var bo = BusinessObjectBuilder.Build(outcome.Entry!, tree);

        Assert.Null(bo.Fields["UnitPrice"]);   // 값이 실리지 않는다
    }
}

public class CorrectionCascadeTests
{
    private sealed class FakeMapper : IAiMapper
    {
        public int Calls;
        public Task<MappingInference> InferAsync(string hint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new MappingInference("PurchaseRequest",
                new() { new FieldMapping("n2", "Material", 0.5, "ai", false) }));   // 저신뢰
        }
    }

    [Fact]
    public async Task Correction_TakesPrecedence_InCascade_WithoutCallingAi()
    {
        var cache = new InMemoryMappingCache();
        var mapper = new FakeMapper();
        var resolver = new MappingResolver(cache, mapper, thetaHigh: 0.8);
        var svc = new PersonalizationService(cache, new KnowledgeStore());

        var screen = new ScreenInfo("https://proc/pr/create", "구매요청", null);
        var tree = new UiNode("n1", "Form", null, null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
        });
        var sig = SignatureService.Compute(screen, tree);

        svc.ApplyCorrection("user1", new CorrectionRequest(
            sig, "PurchaseRequest", new() { new CorrectionField("n2", "Material") }));

        var result = await resolver.ResolveAsync(
            sig, "user1", screen, tree, KnowledgeSeed.Compile(), "PurchaseRequest");

        Assert.True(result.CacheHit);
        Assert.Equal("personal:user1", result.Entry.Scope);
        Assert.Equal("user", result.Entry.Mapping[0].Provenance);
        Assert.Equal(0, mapper.Calls);                  // AI 미호출(개인 보정 우선)
    }

    [Fact]
    public async Task OneUsersCorrection_DoesNotLeakTo_AnotherUser()
    {
        var cache = new InMemoryMappingCache();
        var mapper = new FakeMapper();
        var resolver = new MappingResolver(cache, mapper, thetaHigh: 0.8);
        var svc = new PersonalizationService(cache, new KnowledgeStore());

        var screen = new ScreenInfo("https://proc/pr/create", "구매요청", null);
        var tree = new UiNode("n1", "Form", null, null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
        });
        var sig = SignatureService.Compute(screen, tree);

        svc.ApplyCorrection("alice", new CorrectionRequest(
            sig, "PurchaseRequest", new() { new CorrectionField("n2", "Material") }));

        var bob = await resolver.ResolveAsync(
            sig, "bob", screen, tree, KnowledgeSeed.Compile(), "PurchaseRequest");

        Assert.NotEqual("personal:alice", bob.Entry.Scope);   // 개인 보정은 개인 평면에만(D5)
        Assert.Equal(1, mapper.Calls);                        // bob은 자기 경로로 추론
    }
}

public class PersonalizationApiTests
{
    private static WebApplicationFactory<Program> NewServer() => new();

    private static HttpClient ClientAs(WebApplicationFactory<Program> factory, string? user)
    {
        var client = factory.CreateClient();
        if (user is not null) client.DefaultRequestHeaders.Add(RequestUser.Header, user);
        return client;
    }

    private static ObservationEvent Evt(string user) => new(
        EventId: Guid.NewGuid().ToString(),
        SessionId: "sess", UserId: user,
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo("https://proc/pr/create", "구매요청 등록", null),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
            new("n3", "Edit", "수량", "10", "txtQty", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new()));

    [Fact]
    public async Task Observation_Accumulates_Uep_And_PopulatesReviewQueue()
    {
        using var factory = NewServer();
        var ingest = factory.CreateClient();
        await ingest.PostAsJsonAsync("/v1/observations", Evt("alice"));
        await ingest.PostAsJsonAsync("/v1/observations", Evt("alice"));   // 같은 화면 2회

        var resp = await ClientAs(factory, "alice").GetAsync("/v1/uep");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("screens")[0].GetProperty("count").GetInt32());

        // 스텁 매핑은 신뢰도 0.6 < θ 0.8 → pending_review 로 검수 큐에 쌓인다
        using var review = JsonDocument.Parse(
            await factory.CreateClient().GetStringAsync("/v1/review"));
        Assert.True(review.RootElement.GetProperty("entries").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Ontology_Serves_Names_AndAliases_ForTheCorrectionForm()
    {
        using var factory = NewServer();
        using var doc = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/v1/ontology"));

        var concepts = doc.RootElement.GetProperty("concepts").EnumerateArray().ToList();
        Assert.Equal(ProcurementOntology.Concepts.Length, concepts.Count);

        // 사용자는 "UnitPrice"가 아니라 "단가"로 정정한다 — 별칭이 없으면 보정 폼이 쓸모없다
        var unitPrice = Assert.Single(concepts, c => c.GetProperty("name").GetString() == "UnitPrice");
        Assert.Contains(unitPrice.GetProperty("aliases").EnumerateArray(),
            a => a.GetString() == "단가");
        Assert.True(unitPrice.GetProperty("sensitive").GetBoolean());
    }

    [Fact]
    public async Task Uep_Requires_UserHeader()
    {
        using var factory = NewServer();
        var resp = await ClientAs(factory, null).GetAsync("/v1/uep");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Uep_DoesNotServe_AnotherUsersProfile()
    {
        using var factory = NewServer();
        await factory.CreateClient().PostAsJsonAsync("/v1/observations", Evt("alice"));

        // 사용자를 본문·쿼리로 지정할 수단이 없으므로 bob은 alice의 프로파일에 닿을 수 없다.
        var resp = await ClientAs(factory, "bob").GetAsync("/v1/uep");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Correction_Writes_PersonalScope_OfTheCallingUser()
    {
        using var factory = NewServer();
        var req = new CorrectionRequest("sha256:test", "PurchaseRequest",
            new() { new CorrectionField("n2", "Material") });

        var resp = await ClientAs(factory, "carol").PostAsJsonAsync("/v1/correction", req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("personal:carol", doc.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Correction_Requires_UserHeader()
    {
        using var factory = NewServer();
        var resp = await ClientAs(factory, null).PostAsJsonAsync("/v1/correction",
            new CorrectionRequest("sha256:test", "PurchaseRequest",
                new() { new CorrectionField("n2", "Material") }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Correction_Rejects_UnknownConcept_WithBadRequest()
    {
        using var factory = NewServer();
        var resp = await ClientAs(factory, "dave").PostAsJsonAsync("/v1/correction",
            new CorrectionRequest("sha256:test", "PurchaseRequest",
                new() { new CorrectionField("n2", "가격정보") }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("가격정보", doc.RootElement.GetProperty("unknownConcepts")[0].GetString());
    }

    [Fact]
    public async Task Correction_And_Promote_AreRecorded_WithTheirActor()
    {
        using var factory = NewServer();
        await factory.CreateClient().PostAsJsonAsync("/v1/observations", Evt("erin"));

        // 검수 큐에서 하나 골라 승격
        using var queue = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/v1/review"));
        var target = queue.RootElement.GetProperty("entries")[0];
        var promote = new PromoteRequest(
            target.GetProperty("signature").GetString()!,
            target.GetProperty("scope").GetString()!,
            Confidence: 0.95);

        Assert.Equal(HttpStatusCode.OK,
            (await ClientAs(factory, "reviewer-1").PostAsJsonAsync("/v1/review/promote", promote)).StatusCode);

        await ClientAs(factory, "erin").PostAsJsonAsync("/v1/correction",
            new CorrectionRequest("sha256:x", "PurchaseRequest",
                new() { new CorrectionField("n2", "수량") }));   // 별칭

        using var log = JsonDocument.Parse(await factory.CreateClient().GetStringAsync("/v1/decisions"));
        var entries = log.RootElement.GetProperty("entries").EnumerateArray().ToList();

        Assert.Equal(2, log.RootElement.GetProperty("count").GetInt32());
        Assert.Contains(entries, e => e.GetProperty("action").GetString() == "promote"
                                   && e.GetProperty("actor").GetString() == "reviewer-1");
        Assert.Contains(entries, e => e.GetProperty("action").GetString() == "correction"
                                   && e.GetProperty("actor").GetString() == "erin");
    }

    [Fact]
    public async Task Promote_Requires_UserHeader()
    {
        using var factory = NewServer();
        var resp = await ClientAs(factory, null).PostAsJsonAsync("/v1/review/promote",
            new PromoteRequest("sig", "global"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
