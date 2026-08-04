using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

public class UepTransitionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecordVisit_BuildsEdge_BetweenConsecutiveScreens()
    {
        var uep = new UepStore();
        uep.RecordVisit("u1", "pr", T0, "/pr/create", "구매요청 등록");
        uep.RecordVisit("u1", "po", T0.AddSeconds(40), "/po/list", "발주 목록");

        var edge = Assert.Single(uep.Get("u1")!.Transitions);
        Assert.Equal("pr", edge.FromSignature);
        Assert.Equal("po", edge.ToSignature);
        Assert.Equal("발주 목록", edge.ToTitle);       // 해시만 남으면 제안을 문장으로 쓸 수 없다
        Assert.Equal(1, edge.Count);
        Assert.Equal(40, edge.MedianGapSeconds);
    }

    [Fact]
    public void SelfTransitions_AreNotRecorded()
    {
        var uep = new UepStore();
        // 클라이언트는 한 화면을 여러 번 보낸다 — 자기루프를 남기면 그래프가 그것으로 뒤덮인다
        for (var i = 0; i < 10; i++) uep.RecordVisit("u1", "pr", T0.AddSeconds(i * 5));

        var profile = uep.Get("u1")!;
        Assert.Empty(profile.Transitions);
        Assert.Equal(10, profile.Screens[0].Count);    // 방문 횟수는 그대로 센다
    }

    [Fact]
    public void Repeated_Observations_DoNotDistortGap()
    {
        var uep = new UepStore();
        uep.RecordVisit("u1", "pr", T0);
        uep.RecordVisit("u1", "pr", T0.AddSeconds(50));   // 같은 화면을 계속 보고 있었다
        uep.RecordVisit("u1", "po", T0.AddSeconds(60));

        // 화면을 떠난 시점은 마지막 관측 시점이다 → 60초가 아니라 10초
        Assert.Equal(10, Assert.Single(uep.Get("u1")!.Transitions).MedianGapSeconds);
    }

    [Fact]
    public void LongGap_BreaksTheChain()
    {
        var uep = new UepStore(sessionGap: TimeSpan.FromMinutes(30));
        uep.RecordVisit("u1", "pr", T0);
        uep.RecordVisit("u1", "po", T0.AddHours(2));      // 점심 다녀온 뒤

        // 자리 비움을 업무 순서로 학습하면 다음작업 제안이 통째로 거짓이 된다
        Assert.Empty(uep.Get("u1")!.Transitions);
        Assert.Equal(2, uep.Get("u1")!.Screens.Count);    // 방문 자체는 남는다
    }

    [Fact]
    public void MedianGap_IgnoresOneLongOutlier()
    {
        var uep = new UepStore();
        var t = T0;
        foreach (var gap in new[] { 10, 12, 11, 1500, 9 })   // 1500초 = 세션 안이지만 이상치
        {
            uep.RecordVisit("u1", "pr", t);
            t = t.AddSeconds(gap);
            uep.RecordVisit("u1", "po", t);
            t = t.AddSeconds(1);
        }

        // 평균이면 308초. 중앙값이라 11초 — 한 번의 딴짓이 흐름 전체를 규정하지 못한다
        var forward = Assert.Single(uep.NextScreens("u1", "pr"));
        Assert.Equal(11, forward.MedianGapSeconds);
    }

    [Fact]
    public void NextScreens_RequiresRepetition_AndRanksByFrequency()
    {
        var uep = new UepStore();
        var t = T0;
        void Walk(string to)
        {
            uep.RecordVisit("u1", "pr", t);
            t = t.AddSeconds(20);
            uep.RecordVisit("u1", to, t);
            t = t.AddSeconds(20);
        }

        Walk("po");
        Walk("po");
        Walk("po");
        Walk("report");
        Walk("report");
        Walk("oneoff");                                  // 한 번뿐 — 흐름이 아니라 우연

        var next = uep.NextScreens("u1", "pr");
        Assert.Equal(new[] { "po", "report" }, next.Select(n => n.ToSignature));
        Assert.Equal(3, next[0].Count);

        Assert.Empty(uep.NextScreens("u2", "pr"));       // 사용자 간 격리(D5)
        Assert.Empty(uep.NextScreens("u1", "unknown"));
    }

    [Fact]
    public void NextScreens_RespectsLimit()
    {
        var uep = new UepStore();
        var t = T0;
        foreach (var to in new[] { "a", "a", "b", "b", "c", "c" })
        {
            uep.RecordVisit("u1", "hub", t);
            t = t.AddSeconds(10);
            uep.RecordVisit("u1", to, t);
            t = t.AddSeconds(10);
        }

        Assert.Equal(2, uep.NextScreens("u1", "hub", limit: 2).Count);
        Assert.Empty(uep.NextScreens("u1", "hub", limit: 0));
    }
}

public class NextScreenHintTests
{
    [Fact]
    public void Hint_UsesTitle_AndIsNotActionable()
    {
        var hint = GuideService.NextScreenHint("PurchaseRequest",
            new ScreenTransition("pr", "po", "발주 목록", 4, 35.0, DateTimeOffset.UtcNow));

        Assert.Equal("next_screen", hint.Type);
        Assert.Contains("발주 목록", hint.Text);
        Assert.Contains("4회", hint.Text);
        Assert.False(hint.Actionable);                   // Phase 1은 가이드만 — 이동까지 대신하지 않는다
        Assert.Equal(GuideService.SuggestionId("PurchaseRequest", "next_screen", "po"), hint.Id);
    }

    [Fact]
    public void Hint_FallsBackToShortSignature_WhenTitleMissing()
    {
        var hint = GuideService.NextScreenHint("PurchaseRequest",
            new ScreenTransition("pr", "sha256:abcdef0123456789", null, 2, 12.0, DateTimeOffset.UtcNow));

        Assert.Contains("abcdef01", hint.Text);          // 못 알아보는 제안은 수락도 거부도 될 수 없다
        Assert.DoesNotContain("sha256:abcdef0123456789", hint.Text);
    }

    [Fact]
    public void Hint_IsDistinct_PerOriginBusinessObject()
    {
        var transition = new ScreenTransition("x", "po", "발주", 2, 10, DateTimeOffset.UtcNow);

        // 같은 화면으로 가더라도 "구매요청 다음"과 "발주 다음"은 서로 다른 제안이다
        Assert.NotEqual(
            GuideService.NextScreenHint("PurchaseRequest", transition).Id,
            GuideService.NextScreenHint("PurchaseOrder", transition).Id);
    }
}

public class TransitionEndpointTests
{
    private static WebApplicationFactory<Program> NewServer() => new();

    private static ObservationEvent Event(string user, string url, string title) => new(
        EventId: Guid.NewGuid().ToString(),
        SessionId: "sess", UserId: user,
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo(url, title, null),
        Tree: new UiNode("n1", "Window", title, null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new()));

    private static HttpClient ClientAs(WebApplicationFactory<Program> factory, string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RequestUser.Header, user);
        return client;
    }

    [Fact]
    public async Task Uep_Exposes_Transitions_And_Guide_Proposes_NextScreen()
    {
        using var server = NewServer();
        var client = ClientAs(server, "alice");

        // 구매요청 → 발주목록을 두 번 오간다. 두 번째부터 제안 근거가 선다.
        string? lastPr = null;
        for (var i = 0; i < 2; i++)
        {
            var pr = Event("alice", "https://proc/pr/create", "구매요청 등록");
            await client.PostAsJsonAsync("/v1/observations", pr);
            await client.PostAsJsonAsync("/v1/observations",
                Event("alice", "https://proc/po/list", "발주 목록"));
            lastPr = pr.EventId;
        }

        var profile = await client.GetFromJsonAsync<JsonElement>("/v1/uep");
        var edge = Assert.Single(profile.GetProperty("transitions").EnumerateArray()
            .Where(t => t.GetProperty("toTitle").GetString() == "발주 목록"));
        Assert.Equal(2, edge.GetProperty("count").GetInt32());

        // 구매요청 화면의 가이드가 다음 화면을 제안한다 (화면 하나만 봐서는 나올 수 없는 힌트)
        var guide = await client.GetFromJsonAsync<JsonElement>($"/v1/guide?observation_id={lastPr}");
        var hints = guide.GetProperty("nextHints").EnumerateArray().ToList();
        Assert.Contains(hints, h => h.GetProperty("type").GetString() == "next_screen" &&
                                    h.GetProperty("text").GetString()!.Contains("발주 목록"));

        // 그 제안도 수락/거부 측정 대상이다 — 노출이 남는다
        var suggestions = await client.GetFromJsonAsync<JsonElement>("/v1/suggestions");
        Assert.Contains(suggestions.GetProperty("records").EnumerateArray(),
            r => r.GetProperty("text").GetString()!.Contains("발주 목록"));
    }

    [Fact]
    public async Task Guide_ProposesNothing_WhenScreenVisitedOnce()
    {
        using var server = NewServer();
        var client = ClientAs(server, "bob");

        var pr = Event("bob", "https://proc/pr/create", "구매요청 등록");
        await client.PostAsJsonAsync("/v1/observations", pr);
        await client.PostAsJsonAsync("/v1/observations", Event("bob", "https://proc/po/list", "발주 목록"));

        var guide = await client.GetFromJsonAsync<JsonElement>($"/v1/guide?observation_id={pr.EventId}");
        Assert.DoesNotContain(guide.GetProperty("nextHints").EnumerateArray(),
            h => h.GetProperty("type").GetString() == "next_screen");
    }
}
