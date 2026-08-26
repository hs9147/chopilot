using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ChoPilot.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// AI 판단을 사람이 읽을 수 있게 내려보내는 부분.
//
// 매핑은 ref와 정규 개념만 들고 있다(n2 → Vendor). 그것만으로는 판단이 맞는지 볼 수 없다 —
// n2가 화면의 어느 칸이었는지는 화면 쪽에만 있고, 0.60이 통과인지는 θ를 알아야 말할 수 있다.
// ─────────────────────────────────────────────────────────────────────────────
public class JudgmentViewTests
{
    private static ObservationEvent Event(string id, string? vendor, string url = "https://proc/pr/create") => new(
        EventId: id, SessionId: "s", UserId: "hong",
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo(url, "구매요청 등록", null),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, "prCreate", new()
        {
            new("n2", "Edit", "거래처", vendor, "txtVendor", new()),
            new("n3", "Edit", "품목코드", "M-001", "txtMat", new()),
            new("n9", "Group", null, null, "wrapper", new()),   // 라벨도 값도 없는 컨테이너
        }),
        Privacy: new PrivacyInfo("1.0", new()));

    private static JsonElement Screen(JsonElement review) =>
        review.GetProperty("screens").EnumerateObject().First().Value;

    [Fact]
    public async Task Review_CarriesTheScreenLabelsThatTheMappingLacks()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await client.PostAsJsonAsync("/v1/observations", Event("e1", "㈜대한"));

        var review = await client.GetFromJsonAsync<JsonElement>("/v1/review");
        var entry = review.GetProperty("entries").EnumerateArray().First();
        var screen = Screen(review);

        // 검수 큐가 든 것은 ref뿐이다
        var refs = entry.GetProperty("mapping").EnumerateArray()
            .Select(m => m.GetProperty("elementRef").GetString()).ToList();
        Assert.Contains("n2", refs);

        // 그 ref가 화면에서 무엇이었는지는 screens가 채운다
        Assert.Equal("/pr/create", screen.GetProperty("route").GetString());
        Assert.Equal("구매요청 등록", screen.GetProperty("title").GetString());

        var vendor = screen.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("ref").GetString() == "n2");
        Assert.Equal("거래처", vendor.GetProperty("label").GetString());
        Assert.Equal("㈜대한", vendor.GetProperty("value").GetString());
    }

    // 라벨도 값도 없는 노드를 실으면 대조표가 껍데기 행으로 길어지고, 사람이 훑을 것이 늘어난다.
    [Fact]
    public async Task Screens_LeaveOutNodesWithNeitherLabelNorValue()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await client.PostAsJsonAsync("/v1/observations", Event("e1", "㈜대한"));

        var review = await client.GetFromJsonAsync<JsonElement>("/v1/review");
        var refs = Screen(review).GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("ref").GetString()).ToList();

        Assert.Contains("n1", refs);      // 창은 이름이 있다
        Assert.Contains("n2", refs);
        Assert.DoesNotContain("n9", refs);
    }

    // 서명이 같다는 건 구조가 같다는 뜻이라 라벨은 같다. 값만 최근 것이어야 한다.
    [Fact]
    public async Task Screens_UseTheMostRecentObservationOfASignature()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await client.PostAsJsonAsync("/v1/observations", Event("e1", "㈜대한"));
        await client.PostAsJsonAsync("/v1/observations", Event("e2", "㈜한빛"));

        var review = await client.GetFromJsonAsync<JsonElement>("/v1/review");
        var vendor = Screen(review).GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("ref").GetString() == "n2");

        Assert.Equal("㈜한빛", vendor.GetProperty("value").GetString());
    }

    // 0.60이 통과인지 탈락인지는 임계치를 알아야만 말할 수 있다. 화면이 추측하면 안 된다.
    [Fact]
    public async Task Review_ReportsTheThresholdThatTurnsConfidenceIntoAVerdict()
    {
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Mapping:ThetaHigh"] = "0.55" })));

        var review = await server.CreateClient().GetFromJsonAsync<JsonElement>("/v1/review");
        Assert.Equal(0.55, review.GetProperty("thetaHigh").GetDouble(), 3);
    }

    // cacheHit은 두 갈래라 재추론 보류(θ 미만 캐시 재사용)를 AI 호출과 구분하지 못한다.
    // 구분이 없으면 목록이 "매번 AI를 부른다"고 잘못 말한다 — 지표는 1회라고 말하는데.
    [Fact]
    public async Task ObservationList_KeepsTheThreeWaySourceNotJustCacheHit()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await client.PostAsJsonAsync("/v1/observations", Event("e1", "㈜대한"));
        await client.PostAsJsonAsync("/v1/observations", Event("e2", "㈜대한"));

        var list = await client.GetFromJsonAsync<JsonElement>("/v1/observations");
        var sources = list.GetProperty("items").EnumerateArray()
            .Select(o => o.GetProperty("source").GetString()).ToList();

        Assert.Equal(new[] { "ai", "deferred_cache" }, sources);

        // 목록의 AI 건수와 지표의 AI 호출 수가 어긋나면 둘 중 하나는 거짓말이다.
        var metrics = await client.GetFromJsonAsync<JsonElement>("/v1/metrics");
        Assert.Equal(sources.Count(s => s == "ai"), metrics.GetProperty("aiCalls").GetInt32());
        Assert.Equal(sources.Count(s => s == "deferred_cache"),
            metrics.GetProperty("deferredReuses").GetInt32());

        // 시각도 함께 온다 — 축적이 사흘치인지 3분치인지는 시각 없이는 알 수 없다.
        foreach (var o in list.GetProperty("items").EnumerateArray())
            Assert.NotEqual(default, o.GetProperty("capturedAt").GetDateTimeOffset());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AI 추정 이력 — 서 있는 판단의 대장, 그리고 그것을 제외하는 길.
// ─────────────────────────────────────────────────────────────────────────────
public class InferenceHistoryTests
{
    private static ObservationEvent Event(string id, string url = "https://proc/pr/create") => new(
        EventId: id, SessionId: "s", UserId: "hong",
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo(url, "구매요청 등록", null),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, "prCreate", new()
        {
            new("n2", "Edit", "거래처", "㈜대한", "txtVendor", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new()));

    private static HttpRequestMessage As(HttpMethod method, string url, string user, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add(RequestUser.Header, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<JsonElement> Inferences(HttpClient client, string user) =>
        JsonDocument.Parse(await (await client.SendAsync(As(HttpMethod.Get, "/v1/inferences", user)))
            .Content.ReadAsStringAsync()).RootElement;

    // 개인 스코프를 본인 것만 싣는 판단을 하려면 주체가 필요하다.
    [Fact]
    public async Task Inferences_RequireAPrincipal()
    {
        using var server = new WebApplicationFactory<Program>();
        var response = await server.CreateClient().GetAsync("/v1/inferences");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // 검수 큐는 승격되면 사라진다. 그 판단은 계속 쓰이므로 대장에는 남아야 한다.
    [Fact]
    public async Task Inferences_KeepPromotedEntriesThatTheReviewQueueDrops()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await client.PostAsJsonAsync("/v1/observations", Event("e1"));

        var pending = await client.GetFromJsonAsync<JsonElement>("/v1/review");
        var entry = pending.GetProperty("entries").EnumerateArray().First();
        var signature = entry.GetProperty("signature").GetString()!;

        await client.SendAsync(As(HttpMethod.Post, "/v1/review/promote", "hong",
            new PromoteRequest(signature, "global", 1.0)));

        var afterQueue = await client.GetFromJsonAsync<JsonElement>("/v1/review");
        Assert.Empty(afterQueue.GetProperty("entries").EnumerateArray());     // 큐에서는 빠지고

        var history = await Inferences(client, "hong");
        var kept = Assert.Single(history.GetProperty("entries").EnumerateArray());
        Assert.Equal("trusted", kept.GetProperty("status").GetString());      // 대장에는 남는다
    }

    // 검수 큐에서 막아 둔 격리가 이 목록으로 새면 안 된다.
    [Fact]
    public async Task Inferences_ShowOnlyTheRequestersOwnPersonalScope()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await client.PostAsJsonAsync("/v1/observations", Event("e1"));

        var pending = await client.GetFromJsonAsync<JsonElement>("/v1/review");
        var signature = pending.GetProperty("entries").EnumerateArray()
            .First().GetProperty("signature").GetString()!;

        await client.SendAsync(As(HttpMethod.Post, "/v1/correction", "hong",
            new CorrectionRequest(signature, "PurchaseRequest",
                new List<CorrectionField> { new("n2", "거래처") })));

        var mine = await Inferences(client, "hong");
        Assert.Contains(mine.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("scope").GetString() == "personal:hong");

        var theirs = await Inferences(client, "kim");
        Assert.DoesNotContain(theirs.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("scope").GetString()!.StartsWith("personal:", StringComparison.Ordinal));
    }

    // 제외는 되돌리기가 아니라 "다시 물어라"다 — 엔트리와 함께 재추론 백오프의 기준도 사라진다.
    [Fact]
    public async Task Discard_MakesTheNextObservationInferAgain()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        await client.PostAsJsonAsync("/v1/observations", Event("e1"));
        await client.PostAsJsonAsync("/v1/observations", Event("e2"));

        var before = await client.GetFromJsonAsync<JsonElement>("/v1/observations");
        Assert.Equal(new[] { "ai", "deferred_cache" }, before.GetProperty("items").EnumerateArray()
            .Select(o => o.GetProperty("source").GetString()));

        var signature = before.GetProperty("items").EnumerateArray()
            .First().GetProperty("signature").GetString()!;

        var discard = await client.SendAsync(As(HttpMethod.Post, "/v1/inference/discard", "hong",
            new PromoteRequest(signature, "global")));
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);

        await client.PostAsJsonAsync("/v1/observations", Event("e3"));
        var after = await client.GetFromJsonAsync<JsonElement>("/v1/observations");
        Assert.Equal("ai", after.GetProperty("items").EnumerateArray()
            .Last().GetProperty("source").GetString());

        // 공용 판단을 지우는 것은 모두에게 가므로 누가 했는지가 증거로 남아야 한다.
        var decisions = await client.GetFromJsonAsync<JsonElement>("/v1/decisions");
        Assert.Contains(decisions.GetProperty("entries").EnumerateArray(),
            d => d.GetProperty("action").GetString() == "inference_discard"
                 && d.GetProperty("actor").GetString() == "hong");
    }

    // 침묵하는 절단은 "이게 전부"로 읽힌다 — 대장에서 그 오해는
    // "AI가 결정해 둔 것은 이것뿐"이라는 잘못된 확신이 된다.
    [Fact]
    public async Task Inferences_SayHowManyWereCutOff()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/v1/observations", Event($"e{i}", $"https://proc/s{i}/create"));

        var page = JsonDocument.Parse(await (await client.SendAsync(
            As(HttpMethod.Get, "/v1/inferences?limit=2", "hong"))).Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(2, page.GetProperty("entries").GetArrayLength());
        Assert.Equal(3, page.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Discard_RefusesAnotherPersonsPersonalScope()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await client.PostAsJsonAsync("/v1/observations", Event("e1"));

        var pending = await client.GetFromJsonAsync<JsonElement>("/v1/review");
        var signature = pending.GetProperty("entries").EnumerateArray()
            .First().GetProperty("signature").GetString()!;

        await client.SendAsync(As(HttpMethod.Post, "/v1/correction", "hong",
            new CorrectionRequest(signature, "PurchaseRequest",
                new List<CorrectionField> { new("n2", "거래처") })));

        var response = await client.SendAsync(As(HttpMethod.Post, "/v1/inference/discard", "kim",
            new PromoteRequest(signature, "personal:hong")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);   // 존재 여부도 알려주지 않는다

        // 그리고 실제로 지워지지 않았다
        var mine = await Inferences(client, "hong");
        Assert.Contains(mine.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("scope").GetString() == "personal:hong");
    }
}
