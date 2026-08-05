using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

public class KnowledgeCompileTests
{
    [Fact]
    public void SeedCompile_Matches_TheFormerlyHardcodedKnowledge()
    {
        // 외부화가 동작을 바꾸면 안 된다 — 시드 컴파일 = 기존 하드코딩과 동일해야 한다.
        var k = KnowledgeSeed.Compile();

        Assert.Equal(1, k.Version);
        Assert.Equal(ProcurementOntology.Concepts.Length, k.Concepts.Length);
        Assert.True(k.ByName("UnitPrice")!.Sensitive);
        Assert.Equal("UnitPrice", k.Resolve("단가")!.Name);
        Assert.Null(k.Resolve("가격정보"));

        Assert.Equal(new[] { "Material", "Quantity", "DeliveryDate", "Vendor" }, k.RequiredFor("PurchaseRequest"));
        Assert.Equal(new[] { "OrderNo", "Vendor", "TotalAmount" }, k.RequiredFor("PurchaseOrder"));
        Assert.Null(k.RequiredFor("Unknown"));

        Assert.Equal("PurchaseOrder", k.ResolveBusinessHint(new ScreenInfo("https://proc/po/list", "목록", null)));
        Assert.Equal("PurchaseOrder", k.ResolveBusinessHint(new ScreenInfo("https://proc/x", "발주 등록", null)));
        Assert.Equal("PurchaseRequest", k.ResolveBusinessHint(new ScreenInfo("https://proc/pr/create", "구매요청", null)));
    }

    [Fact]
    public void Compile_Ignores_DeprecatedAndPending()
    {
        var docs = KnowledgeSeed.Documents.ToList();
        docs[0] = docs[0] with { Status = KnowledgeStatus.Deprecated };

        var k = KnowledgeCompiler.Compile(docs, version: 2);

        Assert.Equal(ProcurementOntology.Concepts.Length - 1, k.Concepts.Length);
    }
}

public class KnowledgeStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static KnowledgeDoc ConceptDoc(string name, bool sensitive = false, params string[] aliases) => new(
        Id: $"concept.{name}", Axis: KnowledgeAxis.Domain, Kind: KnowledgeKind.Curated,
        Type: KnowledgeType.Concept, Scope: "global", Title: $"개념: {name}",
        Concept: new Concept(name, "string", aliases, sensitive),
        Required: null, Hint: null, Body: "테스트 개념", Version: 0,
        Status: KnowledgeStatus.PendingReview, Provenance: KnowledgeProvenance.Seed,
        CreatedBy: "t", ApprovedBy: null, UpdatedAt: T0);

    [Fact]
    public void Approve_Publishes_AndBumpsVersion_AndRecompiles()
    {
        var store = new KnowledgeStore();
        var v0 = store.Current.Version;
        Assert.Null(store.Current.Resolve("결제조건"));

        var (draft, err) = store.Submit(ConceptDoc("PaymentTerms", aliases: new[] { "결제조건" }), "hong", T0);
        Assert.Null(err);
        Assert.Equal(KnowledgeStatus.PendingReview, draft!.Status);
        Assert.Equal(v0, store.Current.Version);          // 제출만으로는 아무것도 바뀌지 않는다

        var (published, err2) = store.Approve(draft.Id, "kim", T0.AddMinutes(1));
        Assert.Null(err2);
        Assert.Equal("kim", published!.ApprovedBy);
        Assert.Equal(v0 + 1, store.Current.Version);
        Assert.Equal("PaymentTerms", store.Current.Resolve("결제조건")!.Name);   // 재배포 없이 지식이 자랐다
    }

    [Fact]
    public void Approve_Rejects_SensitiveDowngrade()
    {
        // UnitPrice(민감)를 비민감으로 바꾸는 개정 — 통과되면 이후 관측에서 단가가 마스킹되지 않는다.
        var store = new KnowledgeStore();
        store.Submit(ConceptDoc("UnitPrice", sensitive: false, "단가"), "mallory", T0);

        var (doc, err) = store.Approve("concept.UnitPrice", "mallory", T0);

        Assert.Null(doc);
        Assert.Contains("하향", err);
        Assert.True(store.Current.ByName("UnitPrice")!.Sensitive);   // 게시본 불변
    }

    [Fact]
    public void Submit_Revision_BumpsDocVersion_AndGetPrefersPending()
    {
        var store = new KnowledgeStore();
        var (draft, _) = store.Submit(ConceptDoc("Material", aliases: new[] { "품목", "품번" }), "hong", T0);

        Assert.Equal(2, draft!.Version);                  // 시드 v1의 개정
        Assert.Equal(KnowledgeStatus.PendingReview, store.Get("concept.Material")!.Status);

        // 승인 전까지 컴파일은 기존 게시본을 쓴다
        Assert.Null(store.Current.Resolve("품번"));
        store.Approve("concept.Material", "kim", T0);
        Assert.Equal("Material", store.Current.Resolve("품번")!.Name);
    }

    [Fact]
    public void Deprecate_Removes_FromCompile_ButKeepsHistory()
    {
        var store = new KnowledgeStore();
        var (doc, err) = store.Deprecate("concept.OrderNo", "kim", T0);

        Assert.Null(err);
        Assert.Equal(KnowledgeStatus.Deprecated, doc!.Status);
        Assert.Null(store.Current.ByName("OrderNo"));                 // 컴파일에서 제외
        Assert.NotNull(store.Get("concept.OrderNo"));                 // 이력은 남는다 — 삭제는 없다

        var (again, err2) = store.Deprecate("concept.OrderNo", "kim", T0);
        Assert.Null(again);
        Assert.Contains("이미", err2);
    }

    [Theory]
    [InlineData("bad axis", "nowhere", KnowledgeType.Concept)]
    [InlineData("bad type", KnowledgeAxis.Domain, "opinion")]
    public void Submit_Rejects_InvalidAxisOrType(string _, string axis, string type)
    {
        var store = new KnowledgeStore();
        var doc = ConceptDoc("X") with { Axis = axis, Type = type };

        var (draft, err) = store.Submit(doc, "t", T0);
        Assert.Null(draft);
        Assert.NotNull(err);
    }

    [Fact]
    public void Submit_Rejects_PayloadMismatch()
    {
        // 스키마 없는 자유 지식은 컴파일할 수 없다 — 타입과 페이로드는 일치해야 한다.
        var store = new KnowledgeStore();
        var (draft, err) = store.Submit(ConceptDoc("X") with { Concept = null }, "t", T0);

        Assert.Null(draft);
        Assert.Contains("concept", err);
    }
}

public class KnowledgeLoopTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class CountingMapper : IAiMapper
    {
        public int Calls;
        public Task<MappingInference> InferAsync(string hint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new MappingInference("PurchaseRequest",
                new() { new FieldMapping("n2", "Material", 0.5, "ai", false) }));   // 저신뢰
        }
    }

    private static readonly ScreenInfo Screen = new("https://proc/pr/create", "구매요청", null);
    private static readonly UiNode Tree = new("n1", "Form", null, null, null, new()
    {
        new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
    });

    private static KnowledgeDoc ConceptDoc(string name, params string[] aliases) => new(
        Id: $"concept.{name}", Axis: KnowledgeAxis.Domain, Kind: KnowledgeKind.Curated,
        Type: KnowledgeType.Concept, Scope: "global", Title: name,
        Concept: new Concept(name, "string", aliases),
        Required: null, Hint: null, Body: "", Version: 0,
        Status: KnowledgeStatus.PendingReview, Provenance: KnowledgeProvenance.Seed,
        CreatedBy: "t", ApprovedBy: null, UpdatedAt: T0);

    [Fact]
    public void CorrectionLoop_UnknownConcept_Publish_ThenAccepted()
    {
        // A단계의 핵심 루프: 보정 거부 → 개념 게시 → 재보정 성공. 배포가 없다.
        var store = new KnowledgeStore();
        var svc = new PersonalizationService(new InMemoryMappingCache(), store);

        var rejected = svc.ApplyCorrection("u1", new CorrectionRequest(
            "sig", "PurchaseRequest", new() { new CorrectionField("n2", "결제조건") }));
        Assert.False(rejected.Accepted);

        store.Submit(ConceptDoc("PaymentTerms", "결제조건"), "hong", T0);
        store.Approve("concept.PaymentTerms", "kim", T0);

        var accepted = svc.ApplyCorrection("u1", new CorrectionRequest(
            "sig", "PurchaseRequest", new() { new CorrectionField("n2", "결제조건") }));
        Assert.True(accepted.Accepted);
        Assert.Equal("PaymentTerms", accepted.Entry!.Mapping[0].Concept);
    }

    [Fact]
    public async Task VersionBump_Expires_ReinferenceBackoff()
    {
        // 백오프의 전제("재추론이 의미 있는 건 온톨로지가 바뀐 뒤")가 실제 사건에 연결된다.
        var store = new KnowledgeStore();
        var mapper = new CountingMapper();
        var now = T0;
        var resolver = new MappingResolver(new InMemoryMappingCache(), mapper,
            thetaHigh: 0.8, reinferAfter: TimeSpan.FromHours(24), clock: () => now);
        var sig = SignatureService.Compute(Screen, Tree);

        Task<MappingResolver.ResolveResult> Resolve() =>
            resolver.ResolveAsync(sig, "u1", Screen, Tree, store.Current, "PurchaseRequest");

        Assert.Equal(MappingResolver.Source.Ai, (await Resolve()).Source);
        now = now.AddMinutes(10);
        Assert.Equal(MappingResolver.Source.DeferredCache, (await Resolve()).Source);   // 백오프 안

        store.Submit(ConceptDoc("PaymentTerms", "결제조건"), "hong", T0);
        store.Approve("concept.PaymentTerms", "kim", T0);   // 지식 버전 증가

        now = now.AddMinutes(1);                            // 시간은 여전히 백오프 안이다
        Assert.Equal(MappingResolver.Source.Ai, (await Resolve()).Source);   // 그래도 다시 묻는다
        Assert.Equal(2, mapper.Calls);

        now = now.AddMinutes(1);
        Assert.Equal(MappingResolver.Source.DeferredCache, (await Resolve()).Source);   // 새 버전으로 다시 보류
    }

    [Fact]
    public void DeprecateConcept_StripsField_AndDemotes_WhenConfidenceFalls()
    {
        var cache = new InMemoryMappingCache();
        var store = new KnowledgeStore();
        var knowledge = new KnowledgeService(store, cache, thetaHigh: 0.8);

        // OrderNo 하나에만 의존하는 trusted 매핑 + 다필드 매핑
        cache.Put(new MappingEntry("sig-only", "global", null, "PurchaseOrder", null,
            new() { new FieldMapping("n2", "OrderNo", 0.9, "ai", false) }, 0.9, "trusted"));
        cache.Put(new MappingEntry("sig-multi", "global", null, "PurchaseOrder", null,
            new()
            {
                new FieldMapping("n2", "OrderNo", 0.9, "ai", false),
                new FieldMapping("n3", "Vendor", 0.95, "ai", false),
            }, 0.92, "trusted"));

        var outcome = knowledge.Deprecate("concept.OrderNo", "kim");

        Assert.Equal(2, outcome.TouchedMappings);

        var only = cache.Get("sig-only", "global")!;
        Assert.Empty(only.Mapping);                          // 폐기 개념으로의 매핑은 지식이 아니다
        Assert.Equal(0, only.Confidence);
        Assert.Equal("pending_review", only.Status);         // θ 미만 → 강등

        var multi = cache.Get("sig-multi", "global")!;
        Assert.Single(multi.Mapping);
        Assert.Equal("Vendor", multi.Mapping[0].Concept);
        Assert.Equal("trusted", multi.Status);               // 남은 필드가 θ 이상이면 유지
    }
}

public class KnowledgeApiTests
{
    private static WebApplicationFactory<Program> NewServer() => new();

    private static HttpRequestMessage WithUser(HttpMethod method, string url, string? user, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (user is not null) req.Headers.Add(RequestUser.Header, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static object ConceptDocJson(string name, string alias, string scope = "global") => new
    {
        id = $"concept.{name}",
        axis = "domain",
        kind = "curated",
        type = "concept",
        scope,
        title = $"개념: {name}",
        concept = new { name, type = "string", aliases = new[] { alias }, sensitive = false },
        body = "테스트",
        version = 0,
        status = "pending_review",
        provenance = new { signalRefs = new[] { "test" }, supportCount = 3, distinctUsers = 2, lastObserved = (string?)null },
        createdBy = "ignored",
        approvedBy = (string?)null,
        updatedAt = "2026-08-01T00:00:00Z",
    };

    [Fact]
    public async Task Ontology_Grows_WithoutRedeploy_ViaSubmitAndApprove()
    {
        using var server = NewServer();
        var client = server.CreateClient();

        var before = await client.GetFromJsonAsync<JsonElement>("/v1/ontology");
        var v0 = before.GetProperty("version").GetInt32();

        var submit = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge", "hong",
            ConceptDocJson("PaymentTerms", "결제조건")));
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var approve = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/concept.PaymentTerms/approve", "kim"));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/v1/ontology");
        Assert.Equal(v0 + 1, after.GetProperty("version").GetInt32());
        Assert.Contains(after.GetProperty("concepts").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "PaymentTerms");

        // 결정 이력에 승인자가 남는다
        var decisions = await client.GetFromJsonAsync<JsonElement>("/v1/decisions");
        Assert.Contains(decisions.GetProperty("entries").EnumerateArray(),
            d => d.GetProperty("action").GetString() == "knowledge_publish" &&
                 d.GetProperty("actor").GetString() == "kim");
    }

    [Fact]
    public async Task SubmitAndApprove_Require_UserHeader()
    {
        using var server = NewServer();
        var client = server.CreateClient();

        var submit = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge", null,
            ConceptDocJson("X", "엑스")));
        Assert.Equal(HttpStatusCode.BadRequest, submit.StatusCode);

        var approve = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/concept.Material/approve", null));
        Assert.Equal(HttpStatusCode.BadRequest, approve.StatusCode);
    }

    [Fact]
    public async Task RequiredFieldsRule_Changes_Guide_WithoutRedeploy()
    {
        using var server = NewServer();
        var client = server.CreateClient();

        // 관측 → 가이드: 시드 규칙(필수 4개) 기준
        var evt = new ObservationEvent(
            EventId: Guid.NewGuid().ToString(), SessionId: "s", UserId: "alice",
            CapturedAt: DateTimeOffset.UtcNow,
            Screen: new ScreenInfo("https://proc/pr/create", "구매요청 등록", null),
            Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, new()
            {
                new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
                new("n3", "Edit", "수량", "10", "txtQty", new()),
            }),
            Privacy: new PrivacyInfo("1.0", new()));
        await client.PostAsJsonAsync("/v1/observations", evt);

        var g1 = await client.GetFromJsonAsync<JsonElement>($"/v1/guide?observation_id={evt.EventId}");
        Assert.Equal(4, g1.GetProperty("required").GetInt32());

        // 규칙 개정: 구매요청 필수는 Material·Quantity 둘뿐이라고 게시
        var rule = new
        {
            id = "rule.required.PurchaseRequest",
            axis = "domain",
            kind = "curated",
            type = "required_fields",
            scope = "global",
            title = "구매요청 필수 필드(개정)",
            required = new { businessObject = "PurchaseRequest", concepts = new[] { "Material", "Quantity" } },
            body = "축소",
            version = 0,
            status = "pending_review",
            provenance = new { signalRefs = new[] { "test" }, supportCount = 5, distinctUsers = 3, lastObserved = (string?)null },
            createdBy = "x",
            approvedBy = (string?)null,
            updatedAt = "2026-08-01T00:00:00Z",
        };
        await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge", "hong", rule));
        await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/rule.required.PurchaseRequest/approve", "kim"));

        var g2 = await client.GetFromJsonAsync<JsonElement>($"/v1/guide?observation_id={evt.EventId}");
        Assert.Equal(2, g2.GetProperty("required").GetInt32());   // 재배포 없이 가이드가 바뀌었다
        Assert.Equal(1, g2.GetProperty("ratio").GetDouble());
    }

    [Fact]
    public async Task PersonalScopeDocs_AreHidden_FromOtherUsers()
    {
        using var server = NewServer();
        var client = server.CreateClient();

        await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge", "alice",
            ConceptDocJson("AliceNote", "앨리스", scope: "personal:alice")));

        var alice = await client.SendAsync(WithUser(HttpMethod.Get, "/v1/knowledge?status=pending_review", "alice"));
        var aliceDocs = JsonDocument.Parse(await alice.Content.ReadAsStringAsync());
        Assert.Contains(aliceDocs.RootElement.GetProperty("items").EnumerateArray(),
            d => d.GetProperty("id").GetString() == "concept.AliceNote");

        var bob = await client.SendAsync(WithUser(HttpMethod.Get, "/v1/knowledge?status=pending_review", "bob"));
        var bobDocs = JsonDocument.Parse(await bob.Content.ReadAsStringAsync());
        Assert.DoesNotContain(bobDocs.RootElement.GetProperty("items").EnumerateArray(),
            d => d.GetProperty("id").GetString() == "concept.AliceNote");

        // 상세 조회도 존재 여부를 숨긴다
        var bobGet = await client.SendAsync(WithUser(HttpMethod.Get, "/v1/knowledge/concept.AliceNote", "bob"));
        Assert.Equal(HttpStatusCode.NotFound, bobGet.StatusCode);
    }

    [Fact]
    public async Task Deprecate_Reports_TouchedMappings()
    {
        using var server = NewServer();
        var client = server.CreateClient();

        // 발주 화면 관측으로 OrderNo 매핑을 만든다 (스텁 매퍼가 라벨에서 유추)
        var evt = new ObservationEvent(
            EventId: Guid.NewGuid().ToString(), SessionId: "s", UserId: "alice",
            CapturedAt: DateTimeOffset.UtcNow,
            Screen: new ScreenInfo("https://proc/po/view?id=PO1", "발주 조회", null),
            Tree: new UiNode("n1", "Window", "발주 조회", null, null, new()
            {
                new("n2", "Edit", "발주번호", "PO-1", "txtOrd", new()),
            }),
            Privacy: new PrivacyInfo("1.0", new()));
        await client.PostAsJsonAsync("/v1/observations", evt);

        var resp = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/concept.OrderNo/deprecate", "kim"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("touchedMappings").GetInt32());
        Assert.Equal("deprecated", doc.RootElement.GetProperty("doc").GetProperty("status").GetString());

        // 온톨로지에서도 사라졌다
        var ontology = await client.GetFromJsonAsync<JsonElement>("/v1/ontology");
        Assert.DoesNotContain(ontology.GetProperty("concepts").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "OrderNo");
    }
}
