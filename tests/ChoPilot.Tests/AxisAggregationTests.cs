using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

public class UnknownConceptSignalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RejectedCorrection_IsRecorded_AsSignal()
    {
        // 거부는 옳지만 버려지면 안 된다 — 그 시도가 온톨로지 결핍의 증거다.
        var log = new UnknownConceptLog();
        var svc = new PersonalizationService(new InMemoryMappingCache(), new KnowledgeStore(), log);

        var outcome = svc.ApplyCorrection("u1", new CorrectionRequest(
            "sig", "PurchaseRequest", new() { new CorrectionField("n2", "결제조건") }));

        Assert.False(outcome.Accepted);
        Assert.Equal(1, log.Count);
        Assert.Equal("결제조건", log.Snapshot()[0].Term);
        Assert.Equal("u1", log.Snapshot()[0].UserId);
    }

    [Fact]
    public void AcceptedCorrection_RecordsNothing()
    {
        var log = new UnknownConceptLog();
        var svc = new PersonalizationService(new InMemoryMappingCache(), new KnowledgeStore(), log);

        svc.ApplyCorrection("u1", new CorrectionRequest(
            "sig", "PurchaseRequest", new() { new CorrectionField("n2", "단가") }));

        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void Candidates_FoldByTerm_AndCountDistinctUsers()
    {
        var log = new UnknownConceptLog();
        log.Record("u1", "s1", "PurchaseRequest", new[] { "결제조건" }, T0);
        log.Record("u2", "s1", "PurchaseRequest", new[] { "결제조건" }, T0.AddMinutes(1));
        log.Record("u1", "s2", "PurchaseOrder", new[] { "결제조건" }, T0.AddMinutes(2));
        log.Record("u1", "s1", "PurchaseRequest", new[] { "납품장소" }, T0);

        var candidates = log.Candidates();

        var payment = Assert.Single(candidates, c => c.Term == "결제조건");
        Assert.Equal(3, payment.Attempts);
        Assert.Equal(2, payment.DistinctUsers);
        Assert.Equal(new[] { "PurchaseOrder", "PurchaseRequest" }, payment.BusinessObjects);
        Assert.Equal("결제조건", candidates[0].Term);   // 시도 많은 순
    }
}

public class AxisAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static AxisAggregator Build(
        out UnknownConceptLog unknown, out UepStore uep,
        out SuggestionFeedbackStore suggestions, out KnowledgeStore knowledge,
        int minSupport = 3, int minUsers = 2)
    {
        unknown = new UnknownConceptLog();
        uep = new UepStore();
        suggestions = new SuggestionFeedbackStore();
        knowledge = new KnowledgeStore();
        return TestFoundation.Aggregator(unknown, uep, suggestions, knowledge, new EntityStore(),
            minSupport: minSupport, minUsers: minUsers);
    }

    private static AxisAggregator WithEntities(EntityStore entities, KnowledgeStore knowledge) =>
        TestFoundation.Aggregator(new UnknownConceptLog(), new UepStore(),
            new SuggestionFeedbackStore(), knowledge, entities);

    [Fact]
    public void UnknownConcept_BecomesDraft_WhenBothGatesPass()
    {
        var agg = Build(out var unknown, out _, out _, out _);
        for (var i = 0; i < 2; i++) unknown.Record("u1", "s", "PurchaseRequest", new[] { "결제조건" }, T0);
        unknown.Record("u2", "s", "PurchaseRequest", new[] { "결제조건" }, T0);

        var result = agg.Aggregate(T0);

        var draft = Assert.Single(result.Drafts, d => d.Id == "concept.결제조건");
        Assert.Equal(KnowledgeStatus.PendingReview, draft.Status);
        Assert.Equal("aggregator", draft.CreatedBy);
        Assert.Equal(3, draft.Provenance.SupportCount);
        Assert.Equal(2, draft.Provenance.DistinctUsers);

        // 민감 여부를 모르면 민감으로 제안한다 — 잘못 열어두는 쪽이 훨씬 비싸다
        Assert.True(draft.Concept!.Sensitive);
        Assert.Contains("민감 여부", draft.Body);
    }

    [Fact]
    public void SingleUserPattern_DoesNotBecomeOrgDraft()
    {
        // 한 사람에게서만 나온 패턴을 org로 올리면 그 사람의 활동이 유출된다(k인 게이트).
        var agg = Build(out var unknown, out _, out _, out _);
        for (var i = 0; i < 10; i++) unknown.Record("u1", "s", "PurchaseRequest", new[] { "결제조건" }, T0);

        var result = agg.Aggregate(T0);

        Assert.Empty(result.Drafts);
        Assert.Contains(result.Skipped, s => s.Contains("k인 게이트"));
    }

    [Fact]
    public void LowSupport_IsSkipped_WithReason()
    {
        var agg = Build(out var unknown, out _, out _, out _);
        unknown.Record("u1", "s", "PurchaseRequest", new[] { "결제조건" }, T0);
        unknown.Record("u2", "s", "PurchaseRequest", new[] { "결제조건" }, T0);   // 2회 < 3

        var result = agg.Aggregate(T0);

        Assert.Empty(result.Drafts);
        // 침묵하는 절단은 "다 훑었다"로 읽힌다 — 왜 걸렀는지 보고한다
        Assert.Contains(result.Skipped, s => s.Contains("시도 2회"));
    }

    [Fact]
    public void DeprecatedDoc_IsNeverReproposed()
    {
        // 사람이 폐기한 것을 집계기가 매번 되살리면 검수 큐가 무한 잔소리가 된다.
        var agg = Build(out var unknown, out _, out _, out var knowledge);
        for (var i = 0; i < 3; i++)
            unknown.Record($"u{i}", "s", "PurchaseRequest", new[] { "결제조건" }, T0);

        var first = agg.Aggregate(T0);
        Assert.Single(first.Drafts);

        knowledge.Submit(first.Drafts[0], "aggregator", T0);
        knowledge.Approve("concept.결제조건", "kim", T0);
        knowledge.Deprecate("concept.결제조건", "kim", T0);   // 사람이 "아니다"라고 판단

        var second = agg.Aggregate(T0.AddDays(1));
        Assert.Empty(second.Drafts);
        Assert.Contains(second.Skipped, s => s.Contains("이미 존재"));
    }

    [Fact]
    public void SharedTransition_BecomesFlowDraft_WithRouteNotTitle()
    {
        var agg = Build(out _, out var uep, out _, out _);

        // 두 사용자가 같은 경로를 밟는다. 제목에는 레코드가 섞여 있다.
        foreach (var (user, i) in new[] { ("alice", 0), ("bob", 1), ("alice", 2) })
        {
            var t = T0.AddMinutes(i * 10);
            uep.RecordVisit(user, "sig-pr", t, "/pr/create", "구매요청 등록");
            uep.RecordVisit(user, "sig-po", t.AddSeconds(30), "/po/list", $"발주 목록 - PO{i}");
        }

        var result = agg.Aggregate(T0);

        var flow = Assert.Single(result.Drafts, d => d.Id.StartsWith("note.flow."));
        Assert.Contains("/pr/create", flow.Body);
        Assert.Contains("/po/list", flow.Body);
        Assert.DoesNotContain("PO0", flow.Body);          // 레코드가 섞인 제목은 org 문서에 싣지 않는다
        Assert.DoesNotContain("발주 목록", flow.Body);
        Assert.Equal(2, flow.Provenance.DistinctUsers);
    }

    [Fact]
    public void RepeatedlyRejectedSuggestion_BecomesRuleReviewDraft()
    {
        var agg = Build(out _, out _, out var suggestions, out _);

        var hint = new GuideHint(
            GuideService.SuggestionId("PurchaseRequest", "guide", "DeliveryDate"),
            "guide", "DeliveryDate", "납기 입력이 남았습니다", false);

        foreach (var user in new[] { "alice", "bob", "carol" })
        {
            suggestions.RecordImpressions(user, $"obs-{user}", "sig", "PurchaseRequest", new[] { hint }, T0);
            suggestions.Decide(user, $"obs-{user}", hint.Id, SuggestionOutcome.Rejected, T0);
        }

        var result = agg.Aggregate(T0);

        var draft = Assert.Single(result.Drafts, d => d.Id.StartsWith("note.rejected."));
        Assert.Contains("DeliveryDate", draft.Title);
        Assert.Equal(3, draft.Provenance.DistinctUsers);
        // 거부가 몰린다는 건 사용자가 게으른 게 아니라 규칙이 틀렸다는 신호일 수 있다
        Assert.Contains("필수 필드가 맞는가", draft.Body);
    }

    [Fact]
    public void NoSignals_ProducesNothing()
    {
        var agg = Build(out _, out _, out _, out _);
        var result = agg.Aggregate(T0);

        Assert.Empty(result.Drafts);
        Assert.Empty(result.Skipped);
    }
}

public class KnowledgeViewRendererTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Render_ReturnsNull_ForUnobservedUser() =>
        Assert.Null(new KnowledgeViewRenderer(new UepStore(), new SuggestionFeedbackStore())
            .Render("nobody", T0));

    [Fact]
    public void Render_Describes_Usage_Transitions_AndJudgments()
    {
        var uep = new UepStore();
        var suggestions = new SuggestionFeedbackStore();

        uep.RecordVisit("alice", "sig-pr", T0, "/pr/create", "구매요청 등록");
        uep.RecordVisit("alice", "sig-po", T0.AddSeconds(20), "/po/list", "발주 목록");
        uep.RecordVisit("alice", "sig-pr", T0.AddMinutes(5), "/pr/create", "구매요청 등록");
        uep.RecordVisit("alice", "sig-po", T0.AddMinutes(5).AddSeconds(20), "/po/list", "발주 목록");

        var hint = new GuideHint("sg:x", "guide", "DeliveryDate", "납기 입력이 남았습니다", false);
        foreach (var obs in new[] { "o1", "o2" })
        {
            suggestions.RecordImpressions("alice", obs, "sig-pr", "PurchaseRequest", new[] { hint }, T0);
            suggestions.Decide("alice", obs, hint.Id, SuggestionOutcome.Rejected, T0);
        }

        var doc = new KnowledgeViewRenderer(uep, suggestions).Render("alice", T0)!;

        Assert.Equal(KnowledgeKind.View, doc.Kind);
        Assert.Equal(KnowledgeAxis.User, doc.Axis);
        Assert.Equal("personal:alice", doc.Scope);
        Assert.Null(doc.ApprovedBy);                       // 파생 뷰는 승인 대상이 아니다
        Assert.Contains("구매요청 등록", doc.Body);
        Assert.Contains("발주 목록", doc.Body);
        Assert.Contains("2회 거부", doc.Body);
        Assert.Contains("중단 후보", doc.Body);            // 반복 거부는 그만 보여줄 근거다
    }

    [Fact]
    public void Render_IsDeterministic_AndNotStored()
    {
        var uep = new UepStore();
        var suggestions = new SuggestionFeedbackStore();
        var renderer = new KnowledgeViewRenderer(uep, suggestions);
        uep.RecordVisit("alice", "sig-pr", T0, "/pr/create", "구매요청 등록");

        var a = renderer.Render("alice", T0)!;
        var b = renderer.Render("alice", T0)!;
        Assert.Equal(a.Body, b.Body);

        uep.RecordVisit("alice", "sig-pr", T0.AddMinutes(1), "/pr/create", "구매요청 등록");
        var c = renderer.Render("alice", T0)!;
        Assert.NotEqual(a.Body, c.Body);   // 스토어가 바뀌면 다음 렌더가 다르다 — 저장본이 아니다
    }
}

public class AggregationApiTests
{
    private static WebApplicationFactory<Program> NewServer() => new();

    private static HttpRequestMessage WithUser(HttpMethod method, string url, string? user, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (user is not null) req.Headers.Add(RequestUser.Header, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static CorrectionRequest Correction(string term) =>
        new("sig-x", "PurchaseRequest", new() { new CorrectionField("n2", term) });

    [Fact]
    public async Task FullLoop_RejectedCorrections_Aggregate_Approve_ThenAccepted()
    {
        using var server = NewServer();
        var client = server.CreateClient();

        // 세 사람이 같은 미지 개념으로 정정을 시도한다 → 전부 거부되지만 신호는 남는다
        foreach (var user in new[] { "alice", "bob", "carol" })
        {
            var resp = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/correction", user, Correction("결제조건")));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        var signals = await client.GetFromJsonAsync<JsonElement>("/v1/knowledge/signals");
        Assert.Equal(3, signals.GetProperty("unknownConceptAttempts").GetInt32());

        // 집계 → 초안. 게시는 되지 않는다 — 자동 게시는 없다.
        var agg = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/aggregate", "ops"));
        using var aggDoc = JsonDocument.Parse(await agg.Content.ReadAsStringAsync());
        Assert.Equal(1, aggDoc.RootElement.GetProperty("submitted").GetInt32());

        var ontologyBefore = await client.GetFromJsonAsync<JsonElement>("/v1/ontology");
        Assert.DoesNotContain(ontologyBefore.GetProperty("concepts").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "결제조건");

        // 사람이 승인해야 온톨로지가 된다
        var approve = await client.SendAsync(
            WithUser(HttpMethod.Post, "/v1/knowledge/concept.결제조건/approve", "kim"));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var accepted = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/correction", "alice", Correction("결제조건")));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Aggregate_DryRun_SubmitsNothing()
    {
        using var server = NewServer();
        var client = server.CreateClient();
        foreach (var user in new[] { "alice", "bob", "carol" })
            await client.SendAsync(WithUser(HttpMethod.Post, "/v1/correction", user, Correction("납품장소")));

        var dry = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/aggregate?dryRun=true", "ops"));
        using var doc = JsonDocument.Parse(await dry.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("drafts").GetArrayLength());

        var queue = await client.GetFromJsonAsync<JsonElement>("/v1/knowledge?status=pending_review");
        Assert.Equal(0, queue.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Aggregate_RequiresUserHeader()
    {
        using var server = NewServer();
        var resp = await server.CreateClient()
            .SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/aggregate", null));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task UserAxisView_IsRendered_AndIsolatedPerUser()
    {
        using var server = NewServer();
        var client = server.CreateClient();

        var evt = new ObservationEvent(
            EventId: Guid.NewGuid().ToString(), SessionId: "s", UserId: "alice",
            CapturedAt: DateTimeOffset.UtcNow,
            Screen: new ScreenInfo("https://proc/pr/create", "구매요청 등록", null),
            Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, new()
            {
                new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
            }),
            Privacy: new PrivacyInfo("1.0", new()));
        await client.PostAsJsonAsync("/v1/observations", evt);

        var alice = await client.SendAsync(WithUser(HttpMethod.Get, "/v1/knowledge?axis=user", "alice"));
        using var aliceDoc = JsonDocument.Parse(await alice.Content.ReadAsStringAsync());
        var view = Assert.Single(aliceDoc.RootElement.GetProperty("items").EnumerateArray(),
            d => d.GetProperty("id").GetString() == "view.user.alice");
        Assert.Equal("view", view.GetProperty("kind").GetString());
        Assert.Contains("구매요청 등록", view.GetProperty("body").GetString());

        // 남의 뷰는 목록에도 상세에도 없다(D5)
        var bob = await client.SendAsync(WithUser(HttpMethod.Get, "/v1/knowledge?axis=user", "bob"));
        using var bobDoc = JsonDocument.Parse(await bob.Content.ReadAsStringAsync());
        Assert.DoesNotContain(bobDoc.RootElement.GetProperty("items").EnumerateArray(),
            d => d.GetProperty("id").GetString() == "view.user.alice");

        var bobGet = await client.SendAsync(WithUser(HttpMethod.Get, "/v1/knowledge/view.user.alice", "bob"));
        Assert.Equal(HttpStatusCode.NotFound, bobGet.StatusCode);
    }
}
