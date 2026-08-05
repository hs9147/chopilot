using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

public class ScreenIdentifierTests
{
    [Fact]
    public void Identifies_RecordId_FromUrlQuery()
    {
        var hint = ScreenIdentifier.Identify("https://proc/po/view?id=PO123", "발주 조회");
        Assert.NotNull(hint);
        Assert.Equal("url_query", hint!.Source);
        Assert.Equal("id", hint.Key);
        Assert.Equal("PO123", hint.Value);
    }

    [Fact]
    public void Identifies_RecordId_FromPathSegment_WhenNoQuery()
    {
        var hint = ScreenIdentifier.Identify("https://proc/po/view/PO-2026-001", null);
        Assert.NotNull(hint);
        Assert.Equal("url_path", hint!.Source);
        Assert.Equal("PO-2026-001", hint.Value);
    }

    [Fact]
    public void Identifies_RecordId_FromTitle_WhenUrlHasNoSignal()
    {
        var hint = ScreenIdentifier.Identify("https://proc/po/view", "발주 조회 - PO999");
        Assert.NotNull(hint);
        Assert.Equal("title", hint!.Source);
        Assert.Equal("PO999", hint.Value);
    }

    [Fact]
    public void ReturnsNull_ForNewRecordScreen_WithNoIdSignal()
    {
        // 신규 등록 화면은 레코드ID가 없다(PHASE0-KIT §2.3).
        var hint = ScreenIdentifier.Identify("https://proc/pr/create", "구매요청 등록");
        Assert.Null(hint);
    }
}

public class ConsentPolicyTests
{
    private static ScreenInfo Screen(string? url = "https://proc/pr/create", string? title = "구매요청 등록") =>
        new(url, title, null);

    [Fact]
    public void Blocks_All_When_Disabled()
    {
        var policy = new ConsentPolicy(new ConsentConfig { Enabled = false });
        Assert.False(policy.Evaluate(Screen()).Allowed);
    }

    [Fact]
    public void Blocks_ExcludedApp_ByTitle()
    {
        var policy = new ConsentPolicy(new ConsentConfig { ExcludedApps = new() { "은행" } });
        var d = policy.Evaluate(Screen(title: "인터넷 은행 - 계좌이체"));
        Assert.False(d.Allowed);
        Assert.Contains("은행", d.Reason);
    }

    [Fact]
    public void Blocks_ExcludedUrl()
    {
        var policy = new ConsentPolicy(new ConsentConfig { ExcludedUrlPatterns = new() { "payroll" } });
        Assert.False(policy.Evaluate(Screen(url: "https://intra/payroll/salary")).Allowed);
    }

    [Fact]
    public void Allows_When_NoExclusionMatches()
    {
        var policy = new ConsentPolicy(new ConsentConfig
        {
            ExcludedApps = new() { "은행" },
            ExcludedUrlPatterns = new() { "payroll" }
        });
        Assert.True(policy.Evaluate(Screen()).Allowed);
    }
}

public class EventSpoolTests
{
    private static ObservationEvent Evt(string id, DateTimeOffset at) => new(
        EventId: id, SessionId: "s", UserId: "u", CapturedAt: at,
        Screen: new ScreenInfo("https://proc/pr/create", "t", null),
        Tree: new UiNode("n1", "Form", null, null, null, new()),
        Privacy: new PrivacyInfo("1.0", new()));

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopilot-spool-test-" + Guid.NewGuid().ToString("N"));
        return dir;
    }

    [Fact]
    public async Task Drain_Sends_InFifoOrder_AndDeletes_OnSuccess()
    {
        var dir = TempDir();
        try
        {
            var spool = new EventSpool(dir);
            var t0 = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            spool.Enqueue(Evt("a", t0));
            spool.Enqueue(Evt("b", t0.AddSeconds(1)));
            spool.Enqueue(Evt("c", t0.AddSeconds(2)));
            Assert.Equal(3, spool.PendingCount);

            var order = new List<string>();
            var drain = await spool.DrainAsync(e => { order.Add(e.EventId); return Task.FromResult(true); });

            Assert.Equal(3, drain.Sent);
            Assert.Equal(new[] { "a", "b", "c" }, order);   // FIFO(오래된 순)
            Assert.Equal(0, spool.PendingCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Drain_StopsOnFailure_AndKeepsRemaining()
    {
        var dir = TempDir();
        try
        {
            var spool = new EventSpool(dir);
            var t0 = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            spool.Enqueue(Evt("a", t0));
            spool.Enqueue(Evt("b", t0.AddSeconds(1)));

            // 첫 건만 성공, 두 번째부터 실패
            var count = 0;
            var drain = await spool.DrainAsync(_ => Task.FromResult(count++ == 0));

            Assert.Equal(1, drain.Sent);
            Assert.Equal(1, spool.PendingCount);            // 실패분은 남아 재시도 대상
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public class MappingResolverCacheTests
{
    private sealed class FakeMapper : IAiMapper
    {
        public int Calls;
        public Task<MappingInference> InferAsync(string hint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new MappingInference("PurchaseRequest", new()
            {
                new FieldMapping("n2", "Material", 0.95, "ai", false),
                new FieldMapping("n3", "Quantity", 0.95, "ai", false),
            }));
        }
    }

    [Fact]
    public async Task SecondResolve_HitsCache_WithoutCallingAi()
    {
        var mapper = new FakeMapper();
        var resolver = new MappingResolver(new InMemoryMappingCache(), mapper, thetaHigh: 0.8);
        var screen = new ScreenInfo("https://proc/pr/create", "구매요청", null);
        var tree = new UiNode("n1", "Form", null, null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
            new("n3", "Edit", "수량", "10", "txtQty", new()),
        });
        var sig = SignatureService.Compute(screen, tree);

        var r1 = await resolver.ResolveAsync(sig, "user1", screen, tree, KnowledgeSeed.Compile(), "PurchaseRequest");
        var r2 = await resolver.ResolveAsync(sig, "user1", screen, tree, KnowledgeSeed.Compile(), "PurchaseRequest");

        Assert.False(r1.CacheHit);
        Assert.True(r2.CacheHit);                 // 고신뢰 매핑 → 캐시 재사용
        Assert.Equal(1, mapper.Calls);            // AI는 한 번만 호출(비용 절감)
    }
}

public class AuditServiceTests
{
    private static ObservationEvent Evt() => new(
        "e1", "sess", "user", DateTimeOffset.UtcNow,
        new ScreenInfo("https://proc/pr/create", "구매요청", null),
        new UiNode("n1", "Form", null, null, null, new()),
        new PrivacyInfo("1.0", new() { "n2" }));

    private static MappingEntry Entry() => new(
        "sig", "global", null, "PurchaseRequest", null,
        new() { new FieldMapping("n2", "UnitPrice", 0.9, "ai", true) }, 0.9, "trusted");

    private static MappingResolver.ResolveResult Miss(int inTok = 900, int outTok = 120) =>
        new(Entry(), MappingResolver.Source.Ai, inTok, outTok);

    private static MappingResolver.ResolveResult Hit() =>
        new(Entry(), MappingResolver.Source.TrustedCache);

    /// <summary>저신뢰 캐시 재사용 — 적중도 아니고 AI 호출도 아니다.</summary>
    private static MappingResolver.ResolveResult Deferred() =>
        new(Entry(), MappingResolver.Source.DeferredCache);

    [Fact]
    public void Record_Appends_And_Snapshot_IsNewestFirst()
    {
        var audit = new AuditService();
        audit.Record(Evt(), "sig", Miss(), durationMs: 1200);
        audit.Record(Evt() with { EventId = "e2" }, "sig", Hit(), durationMs: 30);

        Assert.Equal(2, audit.Count);
        var snap = audit.Snapshot();
        Assert.Equal("e2", snap[0].EventId);       // 최신순
        Assert.Equal(2, snap[0].Seq);
        Assert.Equal(1, snap[1].MaskedRefCount);   // 마스킹 카운트 보존
    }

    [Fact]
    public void Metrics_Are_EmptyButValid_BeforeAnyObservation()
    {
        var m = new AuditService().Metrics();

        Assert.Equal(0, m.Observations);
        Assert.Equal(0, m.CacheHitRatio);
        Assert.Empty(m.ByProvenance);
    }

    [Fact]
    public void Metrics_Compute_HitRatio_Percentiles_AndTokens()
    {
        var audit = new AuditService();
        // 미스 1건(AI 호출) + 히트 3건 → 적중률 0.75
        audit.Record(Evt(), "sig-a", Miss(900, 120), durationMs: 2500);
        audit.Record(Evt() with { EventId = "e2" }, "sig-a", Hit(), durationMs: 10);
        audit.Record(Evt() with { EventId = "e3" }, "sig-a", Hit(), durationMs: 20);
        audit.Record(Evt() with { EventId = "e4" }, "sig-b", Hit(), durationMs: 30);

        var m = audit.Metrics();

        Assert.Equal(4, m.Observations);
        Assert.Equal(3, m.CacheHits);
        Assert.Equal(0.75, m.CacheHitRatio);            // H3b 통과선 ≥0.95와 직접 비교 가능
        Assert.Equal(2, m.DistinctSignatures);

        Assert.Equal(1, m.AiCalls);                     // 실제 Bedrock 호출만
        Assert.Equal(900, m.InputTokens);               // H6 비용 원자료
        Assert.Equal(120, m.OutputTokens);

        Assert.Equal(20, m.LatencyP50Ms);               // 정렬 [10,20,30,2500] → nearest-rank
        Assert.Equal(2500, m.LatencyP95Ms);             // 느린 AI 경로가 p95에 드러난다
        Assert.Equal(2500, m.LatencyMaxMs);

        Assert.Equal(4, m.MaskedRefs);                  // 이벤트당 마스킹 1건
        Assert.Equal(4, m.ByStatus["trusted"]);
    }

    [Fact]
    public void Metrics_DoNotCount_DeferredReuse_AsAiCall()
    {
        var audit = new AuditService();
        audit.Record(Evt(), "sig", Miss(900, 120), durationMs: 2500);
        audit.Record(Evt() with { EventId = "e2" }, "sig", Deferred(), durationMs: 5);
        audit.Record(Evt() with { EventId = "e3" }, "sig", Deferred(), durationMs: 5);

        var m = audit.Metrics();

        // 미스를 AI 호출로 세면 3건이 된다 — 실제로는 1번만 물어봤다
        Assert.Equal(1, m.AiCalls);
        Assert.Equal(2, m.DeferredReuses);
        Assert.Equal(0, m.CacheHits);                   // 저신뢰 재사용은 적중이 아니다(H3b 부풀림 방지)
        Assert.Equal(900, m.InputTokens);
    }
}

public class ServerEndToEndTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public ServerEndToEndTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static ObservationEvent SampleEvent() => new(
        EventId: Guid.NewGuid().ToString(),
        SessionId: "sess", UserId: "user-hash",
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo("https://proc/pr/create?id=PR777", "구매요청 등록",
            new RecordHint("url_query", "id", "PR777")),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
            new("n3", "Edit", "수량", "10", "txtQty", new()),
            new("n4", "Edit", "거래처", "A사", "txtVendor", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new()));

    [Fact]
    public async Task Post_Observation_Then_Guide_And_Audit_Flow()
    {
        var client = _factory.CreateClient();

        // 1) 관측 수집
        var evt = SampleEvent();
        var post = await client.PostAsJsonAsync("/v1/observations", evt);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using var postDoc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        Assert.Equal("accepted", postDoc.RootElement.GetProperty("status").GetString());

        // 2) Guide 조회 (읽기 전용)
        var guide = await client.GetAsync($"/v1/guide?observation_id={evt.EventId}");
        Assert.Equal(HttpStatusCode.OK, guide.StatusCode);
        using var guideDoc = JsonDocument.Parse(await guide.Content.ReadAsStringAsync());
        Assert.Equal("PurchaseRequest", guideDoc.RootElement.GetProperty("businessObject").GetString());

        // 3) 감사 로그에 기록됨
        var audit = await client.GetAsync("/v1/audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using var auditDoc = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
        Assert.True(auditDoc.RootElement.GetProperty("count").GetInt32() >= 1);
    }

    [Fact]
    public async Task Guide_ReturnsNotFound_ForUnknownObservation()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/v1/guide?observation_id=does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

public class ServerMetricsTests
{
    // 감사 로그는 싱글턴이라 다른 테스트와 공유하면 집계가 오염된다 → 테스트마다 새 서버.
    private static WebApplicationFactory<Program> NewServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            // StubAiMapper의 필드 신뢰도는 0.6이다. 기본 θ_high(0.8)에서는 매핑이 pending_review로
            // 남아 캐시가 절대 적중하지 않으므로, 캐시 경로를 검증하려면 임계치를 낮춰야 한다.
            b.UseSetting("Mapping:ThetaHigh", "0.5"));

    private static ObservationEvent Observation(string eventId) => new(
        EventId: eventId, SessionId: "sess", UserId: "user-hash",
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo("https://proc/pr/create", "구매요청 등록", null),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
            new("n3", "Edit", "수량", "10", "txtQty", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new() { "n9" }));

    [Fact]
    public async Task Metrics_Report_CacheHit_OnSecondVisitToSameScreen()
    {
        using var factory = NewServer();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/v1/observations", Observation("m1"));   // 미스 → AI(스텁)
        await client.PostAsJsonAsync("/v1/observations", Observation("m2"));   // 동일 서명 → 히트

        var resp = await client.GetAsync("/v1/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("observations").GetInt32());
        Assert.Equal(1, root.GetProperty("cacheHits").GetInt32());
        Assert.Equal(0.5, root.GetProperty("cacheHitRatio").GetDouble());
        Assert.Equal(1, root.GetProperty("distinctSignatures").GetInt32());   // 서명이 갈리지 않았다
        Assert.Equal(1, root.GetProperty("aiCalls").GetInt32());
        Assert.Equal(2, root.GetProperty("maskedRefs").GetInt32());
        Assert.True(root.GetProperty("latencyP95Ms").GetDouble() >= 0);
    }

    [Fact]
    public async Task Metrics_AreServed_BeforeAnyObservation()
    {
        using var factory = NewServer();
        var resp = await factory.CreateClient().GetAsync("/v1/metrics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("observations").GetInt32());
    }
}

/// <summary>측정 UI가 의존하는 조회 API (PHASE0-MEASUREMENT).</summary>
public class ServerMeasurementApiTests
{
    private static WebApplicationFactory<Program> NewServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Mapping:ThetaHigh", "0.5"));

    private static ObservationEvent Observation(string id, string url, List<UiNode> fields, List<string> maskedRefs) => new(
        EventId: id, SessionId: "sess", UserId: "user",
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo(url, "구매요청 등록", ScreenIdentifier.Identify(url)),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, fields),
        Privacy: new PrivacyInfo("1.0", maskedRefs));

    [Fact]
    public async Task Observations_Expose_Inventory_Masking_AndResidualPii()
    {
        using var factory = NewServer();
        var client = factory.CreateClient();

        var evt = Observation("obs-1", "https://proc/pr/create?id=PR1", new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
            new("n3", "Edit", null, PrivacyGate.MaskToken, "txtPrice", new()),
            new("n4", "Text", "메모", "hong@corp.com", null, new()),   // 마스킹을 놓친 값
        }, maskedRefs: new() { "n3" });

        await client.PostAsJsonAsync("/v1/observations", evt);

        using var listDoc = JsonDocument.Parse(await client.GetStringAsync("/v1/observations"));
        var item = listDoc.RootElement.GetProperty("items")[0];

        Assert.Equal(1, listDoc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("/pr/create", item.GetProperty("route").GetString());
        Assert.Equal(4, item.GetProperty("nodeCount").GetInt32());        // 루트 포함
        Assert.Equal(3, item.GetProperty("namedCount").GetInt32());
        Assert.Equal(1, item.GetProperty("maskedCount").GetInt32());
        Assert.Equal(1, item.GetProperty("residualPiiCount").GetInt32()); // H4 반증이 잡아낸다
        Assert.Equal("PR1", item.GetProperty("recordHint").GetProperty("value").GetString());

        using var detailDoc = JsonDocument.Parse(await client.GetStringAsync("/v1/observations/obs-1"));
        var nodes = detailDoc.RootElement.GetProperty("nodes").EnumerateArray().ToList();

        Assert.Equal(4, nodes.Count);
        Assert.True(nodes.Single(n => n.GetProperty("ref").GetString() == "n3").GetProperty("masked").GetBoolean());
        Assert.True(nodes.Single(n => n.GetProperty("ref").GetString() == "n4").GetProperty("residualPii").GetBoolean());
        Assert.Equal(1, nodes.Single(n => n.GetProperty("ref").GetString() == "n2").GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task Detail_ReturnsNotFound_ForUnknownObservation()
    {
        using var factory = NewServer();
        var resp = await factory.CreateClient().GetAsync("/v1/observations/nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Signatures_Flag_AScreenThatSplitIntoSeveralSignatures()
    {
        using var factory = NewServer();
        var client = factory.CreateClient();

        var baseFields = new List<UiNode> { new("n2", "Edit", "품목코드", "M-001", "txtMat", new()) };
        var extraFields = new List<UiNode>
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
            new("n3", "Edit", "수량", "10", "txtQty", new()),      // 구조가 다르다 → 서명이 갈린다
        };

        await client.PostAsJsonAsync("/v1/observations", Observation("a", "https://proc/pr/create?id=1", baseFields, new()));
        await client.PostAsJsonAsync("/v1/observations", Observation("b", "https://proc/pr/create?id=2", extraFields, new()));

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/v1/signatures"));
        var route = doc.RootElement.GetProperty("routes")[0];

        Assert.Equal(1, doc.RootElement.GetProperty("splitRoutes").GetInt32());
        Assert.Equal("/pr/create", route.GetProperty("route").GetString());
        Assert.Equal(2, route.GetProperty("observationCount").GetInt32());
        Assert.Equal(2, route.GetProperty("signatureCount").GetInt32());
        Assert.True(route.GetProperty("split").GetBoolean());
    }

    [Fact]
    public async Task MeasurementConsole_IsServed_AtRoot()
    {
        using var factory = NewServer();
        var resp = await factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("측정 콘솔", await resp.Content.ReadAsStringAsync());
    }
}
