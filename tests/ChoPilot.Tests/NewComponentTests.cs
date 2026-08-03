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
            var sent = await spool.DrainAsync(e => { order.Add(e.EventId); return Task.FromResult(true); });

            Assert.Equal(3, sent);
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
            var sent = await spool.DrainAsync(_ => Task.FromResult(count++ == 0));

            Assert.Equal(1, sent);
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

        var r1 = await resolver.ResolveAsync(sig, "user1", screen, tree, ProcurementOntology.Concepts, "PurchaseRequest");
        var r2 = await resolver.ResolveAsync(sig, "user1", screen, tree, ProcurementOntology.Concepts, "PurchaseRequest");

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

    [Fact]
    public void Record_Appends_And_Snapshot_IsNewestFirst()
    {
        var audit = new AuditService();
        audit.Record(Evt(), "sig", Entry(), cacheHit: false);
        audit.Record(Evt() with { EventId = "e2" }, "sig", Entry(), cacheHit: true);

        Assert.Equal(2, audit.Count);
        var snap = audit.Snapshot();
        Assert.Equal("e2", snap[0].EventId);       // 최신순
        Assert.Equal(2, snap[0].Seq);
        Assert.Equal(1, snap[1].MaskedRefCount);   // 마스킹 카운트 보존
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
