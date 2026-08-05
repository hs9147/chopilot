using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

public class SuggestionIdTests
{
    [Fact]
    public void SuggestionId_IsStable_AcrossRenders()
    {
        var a = GuideService.SuggestionId("PurchaseRequest", "guide", "DeliveryDate");
        var b = GuideService.SuggestionId("PurchaseRequest", "guide", "DeliveryDate");
        Assert.Equal(a, b);           // 렌더마다 달라지면 세션을 가로지르는 집계가 불가능하다
        Assert.StartsWith("sg:", a);
    }

    [Fact]
    public void SuggestionId_Differs_ByBusinessObject_And_Subject()
    {
        var pr = GuideService.SuggestionId("PurchaseRequest", "guide", "Vendor");
        var po = GuideService.SuggestionId("PurchaseOrder", "guide", "Vendor");
        var other = GuideService.SuggestionId("PurchaseRequest", "guide", "Quantity");

        Assert.NotEqual(pr, po);      // 같은 개념이라도 업무객체가 다르면 다른 제안이다
        Assert.NotEqual(pr, other);
    }

    [Fact]
    public void Build_AssignsIdAndSubject_ToEveryHint()
    {
        var (entry, tree) = Fixtures.PartiallyFilledPurchaseRequest();
        var guide = GuideService.Build(entry, tree, BusinessObjectBuilder.Build(entry, tree), KnowledgeSeed.Compile());

        var hint = Assert.Single(guide.NextHints);
        Assert.Equal("DeliveryDate", hint.Subject);
        Assert.Equal(GuideService.SuggestionId("PurchaseRequest", "guide", "DeliveryDate"), hint.Id);
    }
}

public class SuggestionFeedbackStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static GuideHint Hint(string subject) => new(
        Id: GuideService.SuggestionId("PurchaseRequest", "guide", subject),
        Type: "guide", Subject: subject, Text: $"{subject} 입력이 남았습니다", Actionable: false);

    private static int Show(SuggestionFeedbackStore store, string user, string obs, params string[] subjects) =>
        store.RecordImpressions(user, obs, "sig", "PurchaseRequest", subjects.Select(Hint), T0);

    [Fact]
    public void ReShowingGuide_DoesNotInflateImpressions()
    {
        var store = new SuggestionFeedbackStore();

        Assert.Equal(2, Show(store, "u1", "obs1", "Vendor", "DeliveryDate"));
        Assert.Equal(0, Show(store, "u1", "obs1", "Vendor", "DeliveryDate"));   // 재조회
        Assert.Equal(2, store.Stats().Impressions);   // 수락률이 폴링 주기의 함수가 되면 안 된다
    }

    [Fact]
    public void ReShowingGuide_DoesNotOverwriteDecision()
    {
        var store = new SuggestionFeedbackStore();
        var vendor = Hint("Vendor").Id;
        Show(store, "u1", "obs1", "Vendor");

        store.Decide("u1", "obs1", vendor, SuggestionOutcome.Accepted, T0.AddMinutes(1));
        Show(store, "u1", "obs1", "Vendor");          // 클라이언트가 가이드를 다시 읽는다

        Assert.Equal(1, store.Stats().Accepted);      // 방금 누른 수락이 사라지면 안 된다
    }

    [Fact]
    public void Decide_Rejects_SuggestionNeverShown()
    {
        var store = new SuggestionFeedbackStore();
        Show(store, "u1", "obs1", "Vendor");

        // 보여준 적 없는 제안 / 다른 관측 / 다른 사용자 — 모두 분모가 없다
        Assert.Null(store.Decide("u1", "obs1", "sg:deadbeef", SuggestionOutcome.Accepted, T0));
        Assert.Null(store.Decide("u1", "obs2", Hint("Vendor").Id, SuggestionOutcome.Accepted, T0));
        Assert.Null(store.Decide("u2", "obs1", Hint("Vendor").Id, SuggestionOutcome.Accepted, T0));

        Assert.Equal(0, store.Stats().Accepted);
    }

    [Fact]
    public void Decide_LastJudgmentWins()
    {
        var store = new SuggestionFeedbackStore();
        var vendor = Hint("Vendor").Id;
        Show(store, "u1", "obs1", "Vendor");

        store.Decide("u1", "obs1", vendor, SuggestionOutcome.Rejected, T0.AddMinutes(1));
        var final = store.Decide("u1", "obs1", vendor, SuggestionOutcome.Accepted, T0.AddMinutes(2));

        Assert.NotNull(final);
        Assert.Equal(SuggestionOutcome.Accepted, final!.Outcome);
        Assert.Equal(T0.AddMinutes(2), final.DecidedAt);

        var stats = store.Stats();
        Assert.Equal(1, stats.Impressions);           // 마음이 바뀐 것이지 두 번 본 것이 아니다
        Assert.Equal(1, stats.Accepted);
        Assert.Equal(0, stats.Rejected);
    }

    [Fact]
    public void Stats_SeparatesAcceptanceRate_FromResponseRate()
    {
        var store = new SuggestionFeedbackStore();
        Show(store, "u1", "obs1", "Vendor", "DeliveryDate");
        Show(store, "u1", "obs2", "Vendor", "Quantity");

        store.Decide("u1", "obs1", Hint("Vendor").Id, SuggestionOutcome.Accepted, T0);
        store.Decide("u1", "obs2", Hint("Vendor").Id, SuggestionOutcome.Rejected, T0);
        store.Decide("u1", "obs1", Hint("DeliveryDate").Id, SuggestionOutcome.Accepted, T0);
        // Quantity는 무시됨 — 거부가 아니다

        var stats = store.Stats();
        Assert.Equal(4, stats.Impressions);
        Assert.Equal(2, stats.Accepted);
        Assert.Equal(1, stats.Rejected);
        Assert.Equal(1, stats.Pending);
        Assert.Equal(0.6667, stats.AcceptanceRate);   // 2/3 — 명시적 판단만의 분모
        Assert.Equal(0.75, stats.ResponseRate);       // 3/4 — 무시를 거부로 세지 않는다

        // 제안별로도 갈린다: 같은 Vendor 제안이 한 번은 수락, 한 번은 거부
        var vendor = Assert.Single(stats.BySuggestion, s => s.Subject == "Vendor");
        Assert.Equal(2, vendor.Impressions);
        Assert.Equal(0.5, vendor.AcceptanceRate);

        var quantity = Assert.Single(stats.BySuggestion, s => s.Subject == "Quantity");
        Assert.Equal(0, quantity.AcceptanceRate);     // 판단이 없으면 0 — 0으로 나누지 않는다
    }

    [Fact]
    public void Stats_IsEmpty_WhenNothingShown()
    {
        var stats = new SuggestionFeedbackStore().Stats();

        Assert.Equal(0, stats.Impressions);
        Assert.Equal(0, stats.AcceptanceRate);        // 0으로 나누지 않는다
        Assert.Equal(0, stats.ResponseRate);
        Assert.Empty(stats.BySuggestion);
    }
}

public class SuggestionFeedbackEndpointTests
{
    // 제안 저장소는 싱글턴이라 테스트 간 공유하면 집계가 오염된다 → 테스트마다 새 서버.
    private static WebApplicationFactory<Program> NewServer() => new();

    private static ObservationEvent Event(string user = "user-1") => new(
        EventId: Guid.NewGuid().ToString(),
        SessionId: "sess", UserId: user,
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo("https://proc/pr/create?id=PR900", "구매요청 등록",
            new RecordHint("url_query", "id", "PR900")),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
            new("n3", "Edit", "수량", "10", "txtQty", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new()));

    /// <summary>관측 1건을 넣고 가이드를 조회해, 첫 제안 ID와 관측 ID를 돌려준다.</summary>
    private static async Task<(string ObservationId, string SuggestionId)> ShowGuide(
        HttpClient client, ObservationEvent evt)
    {
        var post = await client.PostAsJsonAsync("/v1/observations", evt);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var guide = await client.GetAsync($"/v1/guide?observation_id={evt.EventId}");
        Assert.Equal(HttpStatusCode.OK, guide.StatusCode);

        using var doc = JsonDocument.Parse(await guide.Content.ReadAsStringAsync());
        var hint = doc.RootElement.GetProperty("nextHints").EnumerateArray().First();
        return (evt.EventId, hint.GetProperty("id").GetString()!);
    }

    private static HttpRequestMessage Feedback(string user, string obs, string suggestion, string outcome)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/suggestions/feedback")
        {
            Content = JsonContent.Create(new SuggestionFeedbackRequest(obs, suggestion, outcome)),
        };
        req.Headers.Add(RequestUser.Header, user);
        return req;
    }

    [Fact]
    public async Task Guide_RecordsImpression_And_Feedback_Aggregates()
    {
        using var server = NewServer();
        var client = server.CreateClient();

        var evt = Event();
        var (obs, suggestion) = await ShowGuide(client, evt);

        // 아직 판단 전 — 노출만 잡힌다
        var before = await client.GetFromJsonAsync<JsonElement>("/v1/suggestions");
        Assert.True(before.GetProperty("stats").GetProperty("impressions").GetInt32() >= 1);
        Assert.Equal(0, before.GetProperty("stats").GetProperty("accepted").GetInt32());

        var resp = await client.SendAsync(Feedback(evt.UserId, obs, suggestion, "accepted"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/v1/suggestions");
        Assert.Equal(1, after.GetProperty("stats").GetProperty("accepted").GetInt32());
        Assert.Equal(1, after.GetProperty("stats").GetProperty("acceptanceRate").GetDouble());
        Assert.Contains(after.GetProperty("records").EnumerateArray(),
            r => r.GetProperty("suggestionId").GetString() == suggestion &&
                 r.GetProperty("outcome").GetString() == "accepted");
    }

    [Fact]
    public async Task Feedback_RequiresUserHeader()
    {
        using var server = NewServer();
        var client = server.CreateClient();
        var (obs, suggestion) = await ShowGuide(client, Event());

        // 본문이 사용자를 정하면 누구나 남의 판단을 위조할 수 있다 → 주체는 관문에서만 온다
        var resp = await client.PostAsJsonAsync("/v1/suggestions/feedback",
            new SuggestionFeedbackRequest(obs, suggestion, "accepted"));

        // 본문이 틀린 게 아니라 신원이 없다 — 401이지 400이 아니다
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Feedback_RejectsUnknownOutcome()
    {
        using var server = NewServer();
        var client = server.CreateClient();
        var evt = Event();
        var (obs, suggestion) = await ShowGuide(client, evt);

        // '무시'는 보고 대상이 아니다 — 판단의 부재로 이미 표현된다
        var resp = await client.SendAsync(Feedback(evt.UserId, obs, suggestion, "ignored"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var stats = (await client.GetFromJsonAsync<JsonElement>("/v1/suggestions")).GetProperty("stats");
        Assert.Equal(stats.GetProperty("impressions").GetInt32(), stats.GetProperty("pending").GetInt32());
        Assert.Equal(0, stats.GetProperty("responseRate").GetDouble());   // 거부되면 아무것도 남지 않는다
    }

    [Fact]
    public async Task Feedback_RejectsSuggestionShownToAnotherUser()
    {
        using var server = NewServer();
        var client = server.CreateClient();
        var evt = Event("user-1");
        var (obs, suggestion) = await ShowGuide(client, evt);

        var resp = await client.SendAsync(Feedback("user-2", obs, suggestion, "accepted"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        var stats = await client.GetFromJsonAsync<JsonElement>("/v1/suggestions");
        Assert.Equal(0, stats.GetProperty("stats").GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Feedback_IsAccepted_CaseInsensitively()
    {
        using var server = NewServer();
        var client = server.CreateClient();
        var evt = Event();
        var (obs, suggestion) = await ShowGuide(client, evt);

        var resp = await client.SendAsync(Feedback(evt.UserId, obs, suggestion, " Rejected "));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var stats = await client.GetFromJsonAsync<JsonElement>("/v1/suggestions");
        Assert.Equal(1, stats.GetProperty("stats").GetProperty("rejected").GetInt32());
    }
}

internal static class Fixtures
{
    /// <summary>납기만 비어 있는 구매요청 — 힌트가 정확히 1건 나온다.</summary>
    public static (MappingEntry Entry, UiNode Tree) PartiallyFilledPurchaseRequest()
    {
        var tree = new UiNode("n0", "Form", null, null, null, new()
        {
            new("n1", "Edit", "품목코드", "M-001", null, new()),
            new("n2", "Edit", "수량", "10", null, new()),
            new("n3", "Edit", "납기", "", null, new()),
            new("n4", "Edit", "거래처", "A사", null, new()),
        });

        var entry = new MappingEntry(
            Signature: "sig", Scope: "global", UserId: null,
            BusinessObject: "PurchaseRequest", RecordId: new RecordHint("url_query", "id", "PR123"),
            Mapping: new()
            {
                new("n1", "Material", 0.95, "ai", false),
                new("n2", "Quantity", 0.95, "ai", false),
                new("n3", "DeliveryDate", 0.90, "ai", false),
                new("n4", "Vendor", 0.92, "ai", false),
            },
            Confidence: 0.93, Status: "trusted");

        return (entry, tree);
    }
}
