using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 업무 개선 제안 — 작업 이력에서 사람이 할 일을 뽑고, 사람의 결정으로 기준을 다시 뽑는다.
// ─────────────────────────────────────────────────────────────────────────────
public class ProposalScoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    private static ProposalEvidence Evidence(int occurrences, int users, int daysAgo = 0) =>
        new(occurrences, users, Now.AddDays(-daysAgo - 1), Now.AddDays(-daysAgo), new[] { "sig" });

    // 임계치를 겨우 넘긴 근거가 만점이 되면 점수가 게이트와 같은 말을 두 번 하게 되고,
    // 그 위에서 순위를 매길 수 없다 — 그래서 포화점을 임계치의 2배로 둔다.
    [Fact]
    public void Evidence_SaturatesAtTwiceTheThreshold_NotAtIt()
    {
        var criteria = ProposalCriteria.Seed(Now);
        var rule = new KindRule("k", true, MinOccurrences: 5, MinDistinctUsers: 2, MinScore: 0);

        var atThreshold = ProposalScoring.Score(criteria, rule, Evidence(5, 2), 0.5, Now);
        var atDouble = ProposalScoring.Score(criteria, rule, Evidence(10, 4), 0.5, Now);
        var beyond = ProposalScoring.Score(criteria, rule, Evidence(40, 20), 0.5, Now);

        Assert.Equal(0.5, Dim(atThreshold, "근거").Value, 3);
        Assert.Equal(1.0, Dim(atDouble, "근거").Value, 3);
        Assert.Equal(1.0, Dim(beyond, "근거").Value, 3);   // 포화 — 무한히 커지지 않는다
        Assert.True(atDouble.Total > atThreshold.Total);
    }

    // 오래된 근거는 지금의 업무를 말하지 않는다.
    [Fact]
    public void Recency_HalvesAtTheHalfLife()
    {
        var criteria = ProposalCriteria.Seed(Now);
        var rule = new KindRule("k", true, 1, 1, 0);

        var fresh = Dim(ProposalScoring.Score(criteria, rule, Evidence(10, 5), 0.5, Now), "최근성");
        var aged = Dim(ProposalScoring.Score(criteria, rule, Evidence(10, 5, daysAgo: 14), 0.5, Now), "최근성");

        Assert.Equal(1.0, fresh.Value, 2);
        Assert.Equal(0.5, aged.Value, 2);
    }

    // 탈락 사유가 "기준 미달"이면 무엇을 더 모아야 하는지 알 수 없다.
    [Fact]
    public void RejectReasons_NameTheNumberThatFailed()
    {
        var criteria = ProposalCriteria.Seed(Now);
        var rule = new KindRule("k", true, MinOccurrences: 10, MinDistinctUsers: 3, MinScore: 0.9);

        var thin = Evidence(2, 5);
        Assert.Contains("관측 2회 < 기준 10회",
            ProposalScoring.Reject(rule, thin, ProposalScoring.Score(criteria, rule, thin, 1, Now)));

        var solo = Evidence(20, 1);
        Assert.Contains("1명 < 기준 3명",
            ProposalScoring.Reject(rule, solo, ProposalScoring.Score(criteria, rule, solo, 1, Now)));

        var lowScore = Evidence(20, 6);
        Assert.Contains("점수",
            ProposalScoring.Reject(rule, lowScore, ProposalScoring.Score(criteria, rule, lowScore, 0, Now)));

        var off = rule with { Enabled = false, DisabledReason = "계속 기각됨" };
        Assert.Contains("계속 기각됨",
            ProposalScoring.Reject(off, lowScore, ProposalScoring.Score(criteria, off, lowScore, 1, Now)));
    }

    private static ScoreDimension Dim(ProposalScore score, string name) =>
        score.Dimensions.Single(d => d.Name == name);
}

public class ProposalApiTests
{
    private static HttpRequestMessage As(HttpMethod method, string url, string user, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add(RequestUser.Header, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static ObservationEvent Visit(string id, string user, string url, string title, DateTimeOffset at) => new(
        EventId: id, SessionId: $"s-{user}", UserId: user, CapturedAt: at,
        Screen: new ScreenInfo(url, title, null),
        Tree: new UiNode("n1", "Window", title, null, url.Replace("/", "_"), new()
        {
            new("n2", "Edit", "거래처", "㈜대한", "vendor", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new()));

    /// <summary>세 사람이 구매요청 ↔ 발주 사이를 오간 이력을 만든다.</summary>
    private static async Task SeedReworkHistory(HttpClient client)
    {
        var at = DateTimeOffset.UtcNow.AddHours(-3);
        var i = 0;
        foreach (var user in new[] { "hong", "kim", "lee" })
        foreach (var _ in Enumerable.Range(0, 4))
        {
            await client.PostAsJsonAsync("/v1/observations",
                Visit($"e{++i}", user, "https://proc/pr/create", "구매요청 등록", at = at.AddMinutes(2)));
            await client.PostAsJsonAsync("/v1/observations",
                Visit($"e{++i}", user, "https://proc/po/create", "발주 등록", at = at.AddMinutes(2)));
            await client.PostAsJsonAsync("/v1/observations",
                Visit($"e{++i}", user, "https://proc/pr/create", "구매요청 등록", at = at.AddMinutes(2)));
        }
    }

    private static async Task<JsonElement> Generate(HttpClient client) =>
        JsonDocument.Parse(await (await client.SendAsync(
            As(HttpMethod.Post, "/v1/proposals/generate", "ops"))).Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Generate_RequiresAPrincipal()
    {
        using var server = new WebApplicationFactory<Program>();
        var response = await server.CreateClient().PostAsync("/v1/proposals/generate", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Proposals_CarryEvidenceAndAScoreBreakdown()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await SeedReworkHistory(client);

        var result = await Generate(client);
        var rework = result.GetProperty("proposed").EnumerateArray()
            .Single(p => p.GetProperty("kind").GetString() == ProposalKind.Rework);

        // 근거 없는 제안은 제안이 아니라 주장이다.
        var evidence = rework.GetProperty("evidence");
        Assert.True(evidence.GetProperty("occurrences").GetInt32() >= 4);
        Assert.Equal(3, evidence.GetProperty("distinctUsers").GetInt32());

        // 총점만 남기면 왜 그 점수인지 되짚을 수 없다.
        var dimensions = rework.GetProperty("score").GetProperty("dimensions").EnumerateArray()
            .Select(d => d.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "근거", "도달", "최근성", "영향" }, dimensions);
    }

    // 한 사람의 관측만으로 만든 제안은 그 사람의 업무 습관을 팀에 드러낸다 — k인 게이트가 그 경계다.
    [Fact]
    public async Task OnePersonsHistory_DoesNotBecomeATeamProposal()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        var at = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < 12; i++)
        {
            await client.PostAsJsonAsync("/v1/observations",
                Visit($"solo{i}a", "hong", "https://proc/pr/create", "구매요청 등록", at = at.AddMinutes(2)));
            await client.PostAsJsonAsync("/v1/observations",
                Visit($"solo{i}b", "hong", "https://proc/po/create", "발주 등록", at = at.AddMinutes(2)));
        }

        var result = await Generate(client);

        Assert.DoesNotContain(result.GetProperty("proposed").EnumerateArray(),
            p => p.GetProperty("kind").GetString() is ProposalKind.Rework or ProposalKind.WorkflowShortcut);

        // 조용히 사라지지 않는다 — 왜 떨어졌는지가 함께 온다.
        Assert.Contains(result.GetProperty("skipped").EnumerateArray(),
            s => s.GetProperty("reason").GetString()!.Contains("1명 < 기준 2명"));
    }

    [Fact]
    public async Task DecidedProposals_AreNotProposedAgain()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await SeedReworkHistory(client);

        var first = await Generate(client);
        var id = first.GetProperty("proposed").EnumerateArray().First().GetProperty("id").GetString()!;

        await client.SendAsync(As(HttpMethod.Post, $"/v1/proposals/{Uri.EscapeDataString(id)}/decide",
            "hong", new ProposalDecision(false, "지금은 손댈 수 없다")));

        var second = await Generate(client);
        Assert.DoesNotContain(second.GetProperty("proposed").EnumerateArray(),
            p => p.GetProperty("id").GetString() == id);
        Assert.Contains(second.GetProperty("skipped").EnumerateArray(),
            s => s.GetProperty("reason").GetString()!.Contains("이미 기각"));
    }

    [Fact]
    public async Task Decide_RecordsWhoAndWhy()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await SeedReworkHistory(client);

        var id = (await Generate(client)).GetProperty("proposed").EnumerateArray()
            .First().GetProperty("id").GetString()!;

        var response = await client.SendAsync(As(HttpMethod.Post,
            $"/v1/proposals/{Uri.EscapeDataString(id)}/decide", "hong",
            new ProposalDecision(true, "다음 스프린트에 반영")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 두 번째 결정은 거부된다 — 결정된 제안이 되살아나면 채택률이 흔들린다.
        var again = await client.SendAsync(As(HttpMethod.Post,
            $"/v1/proposals/{Uri.EscapeDataString(id)}/decide", "kim", new ProposalDecision(false)));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var decisions = await client.GetFromJsonAsync<JsonElement>("/v1/decisions");
        Assert.Contains(decisions.GetProperty("entries").EnumerateArray(),
            d => d.GetProperty("action").GetString() == "proposal_accepted"
                 && d.GetProperty("actor").GetString() == "hong"
                 && d.GetProperty("detail").GetString()!.Contains("다음 스프린트에 반영"));
    }

    [Fact]
    public async Task ConcurrentGeneration_IsRefused()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await SeedReworkHistory(client);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ =>
                client.SendAsync(As(HttpMethod.Post, "/v1/proposals/generate", "ops"))));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(2, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 자체 평가 — 사람의 결정에서 기준을 다시 뽑는다.
// ─────────────────────────────────────────────────────────────────────────────
public class ProposalTuningTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    private static ProposalEngine Engine(ProposalStore store) => new(
        store, new ObservationStore(), new UepStore(),
        new FoundationReconciler(new EntityStore(), new FoundationStore(Array.Empty<IFoundationSource>())),
        new DecisionLog(), () => Now);

    private static void SeedDecisions(ProposalStore store, string kind, int accepted, int rejected)
    {
        var criteria = store.Current(Now);
        var n = 0;
        foreach (var status in Enumerable.Repeat(ProposalStatus.Accepted, accepted)
                     .Concat(Enumerable.Repeat(ProposalStatus.Rejected, rejected)))
        {
            var id = $"{kind}:seed{n++}";
            store.Put(new Proposal(id, kind, "t", "b",
                new ProposalEvidence(9, 3, Now, Now, Array.Empty<string>()),
                new ProposalScore(0.7, Array.Empty<ScoreDimension>()),
                ProposalStatus.Proposed, Now, criteria.Version));
            store.Decide(id, status, "hong", null, Now);
        }
    }

    // 두세 번 기각됐다고 종류를 끄면 그 종류는 다시 제안되지 않으므로,
    // 기각이 맞았는지 확인할 표본이 영원히 늘지 않는다.
    [Fact]
    public void ThinEvidence_LeavesTheCriteriaAlone_AndSaysSo()
    {
        var store = new ProposalStore();
        SeedDecisions(store, ProposalKind.Rework, accepted: 0, rejected: 3);

        var result = Engine(store).Tune();

        Assert.False(result.Changed);
        Assert.Equal(result.FromVersion, result.ToVersion);
        Assert.Contains(result.Notes, n => n.Contains("rework") && n.Contains("표본이 얇아"));
    }

    [Fact]
    public void RepeatedRejection_RaisesTheBar_AndRecordsWhy()
    {
        var store = new ProposalStore();
        var before = store.Current(Now).RuleFor(ProposalKind.Rework)!.MinScore;
        SeedDecisions(store, ProposalKind.Rework, accepted: 1, rejected: 7);

        var result = Engine(store).Tune();

        Assert.True(result.Changed);
        var change = Assert.Single(result.Changes);
        Assert.Equal("MinScore", change.Field);
        Assert.True(change.To > change.From);

        var after = store.Current(Now);
        Assert.Equal(result.ToVersion, after.Version);
        Assert.True(after.RuleFor(ProposalKind.Rework)!.MinScore > before);
        Assert.Contains("채택률", after.Rationale);

        // 기준은 덮이지 않고 쌓인다 — 예전 잣대를 되짚을 수 있어야 한다.
        Assert.Equal(2, store.CriteriaHistory().Count);
    }

    [Fact]
    public void RepeatedAcceptance_LowersTheBar()
    {
        var store = new ProposalStore();
        var before = store.Current(Now).RuleFor(ProposalKind.MasterGap)!.MinScore;
        SeedDecisions(store, ProposalKind.MasterGap, accepted: 8, rejected: 1);

        Engine(store).Tune();

        Assert.True(store.Current(Now).RuleFor(ProposalKind.MasterGap)!.MinScore < before);
    }

    // 문턱을 끝까지 올렸는데도 기각된다면 점수가 아니라 종류가 틀렸다.
    [Fact]
    public void WhenRaisingTheBarStopsHelping_TheKindIsTurnedOffWithAReason()
    {
        var store = new ProposalStore();
        var engine = Engine(store);

        // 문턱이 상한(0.8)에 닿을 때까지 기각이 반복된다
        for (var round = 0; round < 6; round++)
        {
            SeedDecisions(store, ProposalKind.Rework, accepted: 0, rejected: 6);
            engine.Tune();
        }

        var rule = store.Current(Now).RuleFor(ProposalKind.Rework)!;
        Assert.False(rule.Enabled);
        Assert.Contains("채택률", rule.DisabledReason);   // 이유 없이 꺼진 규칙은 되살릴 근거도 없다
    }

    // 조정 구간 안이면 흔들지 않는다 — 매번 움직이면 기준이 잡음을 따라간다.
    [Fact]
    public void MiddlingAcceptance_IsLeftAlone()
    {
        var store = new ProposalStore();
        var before = store.Current(Now).RuleFor(ProposalKind.ScreenSplit)!.MinScore;
        SeedDecisions(store, ProposalKind.ScreenSplit, accepted: 3, rejected: 3);

        var result = Engine(store).Tune();

        Assert.False(result.Changed);
        Assert.Equal(before, store.Current(Now).RuleFor(ProposalKind.ScreenSplit)!.MinScore);
        Assert.Contains(result.Notes, n => n.Contains("조정 구간"));
    }
}
