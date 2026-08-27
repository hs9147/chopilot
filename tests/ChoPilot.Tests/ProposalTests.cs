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

// ─────────────────────────────────────────────────────────────────────────────
// 종합평가 — 세 항목의 기하평균. 세 축은 더해서 메우는 관계가 아니라
// 전부 있어야 성립하는 조건이다.
// ─────────────────────────────────────────────────────────────────────────────
public class ProposalRatingTests
{
    [Theory]
    [InlineData(0, 5, 5)]
    [InlineData(5, 0, 5)]
    [InlineData(5, 5, 0)]
    [InlineData(0, 0, 0)]
    public void AnyZero_MakesTheCompositeZero(int accuracy, int usefulness, int actionability)
    {
        var rating = new ProposalRating(accuracy, usefulness, actionability);
        Assert.Equal(0, rating.Quality);
    }

    [Fact]
    public void Composite_IsTheGeometricMeanOfTheThree()
    {
        Assert.Equal(3, new ProposalRating(3, 3, 3).Quality, 6);
        Assert.Equal(5, new ProposalRating(5, 5, 5).Quality, 6);
        Assert.Equal(Math.Cbrt(2 * 4 * 5), new ProposalRating(2, 4, 5).Quality, 6);
    }

    // 곱이라 치우침에 벌점이 붙는다 — 산술평균은 한 축의 붕괴를 다른 축이 메워 준다.
    [Fact]
    public void Composite_PunishesImbalance_MoreThanAnArithmeticMeanWould()
    {
        var lopsided = new ProposalRating(5, 5, 1);
        var balanced = new ProposalRating(3, 4, 4);

        Assert.Equal(Math.Cbrt(25), lopsided.Quality, 6);           // ≈ 2.92
        Assert.True(balanced.Quality > lopsided.Quality);           // 산술이면 3.67 > 3.67 로 뒤집힌다
        Assert.True(lopsided.Quality < (5 + 5 + 1) / 3.0);
    }
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
            "hong", new ProposalDecision(false, new ProposalRating(4, 4, 1), "근거는 맞지만 지금은 손댈 수 없다")));

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
            new ProposalDecision(true, new ProposalRating(5, 5, 4), "다음 스프린트에 반영")));
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

    // 범위를 벗어난 평가를 조용히 잘라 넣으면 학습이 없는 값으로 돌아간다.
    [Fact]
    public async Task OutOfRangeRating_IsRefused_AndTheProposalStaysUndecided()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await SeedReworkHistory(client);

        var id = (await Generate(client)).GetProperty("proposed").EnumerateArray()
            .First().GetProperty("id").GetString()!;

        var response = await client.SendAsync(As(HttpMethod.Post,
            $"/v1/proposals/{Uri.EscapeDataString(id)}/decide", "hong", new ProposalDecision(true, new ProposalRating(9, 3, 3))));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // 거부됐으니 아직 결정되지 않았고, 그래서 제대로 된 평가를 다시 낼 수 있다.
        var retry = await client.SendAsync(As(HttpMethod.Post,
            $"/v1/proposals/{Uri.EscapeDataString(id)}/decide", "hong", new ProposalDecision(true, new ProposalRating(4, 4, 4))));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    // 평가와 채택은 다른 질문이다 — "근거는 맞지만 지금 손댈 수 없다"가 흔하다.
    [Fact]
    public async Task RatingIsSeparateFromAcceptance()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await SeedReworkHistory(client);

        var id = (await Generate(client)).GetProperty("proposed").EnumerateArray()
            .First().GetProperty("id").GetString()!;

        await client.SendAsync(As(HttpMethod.Post, $"/v1/proposals/{Uri.EscapeDataString(id)}/decide",
            "hong", new ProposalDecision(false, new ProposalRating(5, 5, 0), "이번 분기엔 손댈 수 없다")));

        var listed = await client.GetFromJsonAsync<JsonElement>("/v1/proposals");
        var decided = listed.GetProperty("items").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == id);

        Assert.Equal("rejected", decided.GetProperty("status").GetString());
        var rating = decided.GetProperty("rating");
        Assert.Equal(5, rating.GetProperty("accuracy").GetInt32());
        Assert.Equal(0, rating.GetProperty("actionability").GetInt32());   // 0부터 시작 — 완전한 부정

        // 기준을 고치는 숫자는 평점 쪽이다.
        var kind = decided.GetProperty("kind").GetString();
        var outcome = listed.GetProperty("outcomes").EnumerateArray()
            .Single(o => o.GetProperty("kind").GetString() == kind);
        Assert.Equal(0, outcome.GetProperty("acceptanceRate").GetDouble());
        Assert.Equal(5, outcome.GetProperty("meanUsefulness").GetDouble());
        Assert.Equal(0, outcome.GetProperty("meanActionability").GetDouble());

        // 종합평가는 기하평균이라 하나라도 0이면 0이다 — 세 축은 더해서 메우는 관계가 아니다.
        // 산술평균이었다면 3.3으로 남아 "괜찮은 제안"처럼 보였을 자리다.
        Assert.Equal(0, outcome.GetProperty("meanQuality").GetDouble());
    }

    // 겹친 생성이 같은 제안을 두 번 만들면 안 된다.
    //
    // 상태 코드의 분포로 단언하지 않는다 — 생성은 I/O가 없어 빠르므로 세 요청이 실제로 겹칠지는
    // 실행 순간에 달렸고, 그걸 단언하면 붙었다 떨어졌다 하는 시험이 된다. 409 계약 자체는
    // SingleFlightApiTests가 막는 게이트로 결정적으로 검증한다. 여기서 지킬 것은 결과의 성질이다.
    [Fact]
    public async Task ConcurrentGeneration_NeverDuplicatesAProposal()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await SeedReworkHistory(client);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 3).Select(_ =>
                client.SendAsync(As(HttpMethod.Post, "/v1/proposals/generate", "ops"))));

        Assert.All(results, r =>
            Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"기대: 200 또는 409, 실제: {(int)r.StatusCode}"));
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.OK);

        var listed = await client.GetFromJsonAsync<JsonElement>("/v1/proposals");
        var ids = listed.GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()).ToList();

        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 자체 평가 — 사람의 결정에서 기준을 다시 뽑는다.
// ─────────────────────────────────────────────────────────────────────────────
public class ProposalTuningTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    private static IEnumerable<ProposalRating> Ratings(
        int count, int accuracy, int usefulness, int actionability = 3) =>
        Enumerable.Repeat(new ProposalRating(accuracy, usefulness, actionability), count);

    private static ProposalEngine Engine(ProposalStore store) => new(
        store, new ObservationStore(), new UepStore(),
        new FoundationReconciler(new EntityStore(), new FoundationStore(Array.Empty<IFoundationSource>())),
        new DecisionLog(), () => Now);

    /// <summary>평가가 달린 결정을 심는다. 축값을 함께 넣어야 가중치 학습도 재현된다.</summary>
    private static void SeedRatings(
        ProposalStore store, string kind, IEnumerable<ProposalRating> ratings,
        Func<int, double>? evidenceAxis = null)
    {
        var criteria = store.Current(Now);
        var n = 0;
        foreach (var rating in ratings)
        {
            var id = $"{kind}:seed{n}";
            var dims = new[]
            {
                new ScoreDimension("근거", evidenceAxis?.Invoke(n) ?? 0.5, criteria.EvidenceWeight, ""),
                new ScoreDimension("도달", 0.5, criteria.ReachWeight, ""),
                new ScoreDimension("최근성", 0.5, criteria.RecencyWeight, ""),
                new ScoreDimension("영향", 0.5, criteria.ImpactWeight, ""),
            };
            store.Put(new Proposal(id, kind, "t", "b",
                new ProposalEvidence(9, 3, Now, Now, Array.Empty<string>()),
                new ProposalScore(0.7, dims),
                ProposalStatus.Proposed, Now, criteria.Version));
            // 채택 여부는 학습에 쓰이지 않는다 — 일부러 평가와 어긋나게 둔다.
            store.Decide(id, ProposalStatus.Rejected, "hong", rating, null, Now);
            n++;
        }
    }

    // 두세 번 낮게 평가됐다고 종류를 끄면 그 종류는 다시 제안되지 않으므로,
    // 그 평가가 맞았는지 확인할 표본이 영원히 늘지 않는다.
    [Fact]
    public void ThinEvidence_LeavesTheCriteriaAlone_AndSaysSo()
    {
        var store = new ProposalStore();
        SeedRatings(store, ProposalKind.Rework, Ratings(3, accuracy: 4, usefulness: 1));

        var result = Engine(store).Tune();

        Assert.False(result.Changed);
        Assert.Equal(result.FromVersion, result.ToVersion);
        Assert.Contains(result.Notes, n => n.Contains("rework") && n.Contains("표본이 얇아"));
    }

    // 채택 여부가 아니라 평점이 기준을 고친다 — 전부 기각이어도 평가가 높으면 문턱은 내려간다.
    [Fact]
    public void HighRatings_LowerTheBar_EvenWhenEverythingWasRejected()
    {
        var store = new ProposalStore();
        var before = store.Current(Now).RuleFor(ProposalKind.MasterGap)!.MinScore;
        SeedRatings(store, ProposalKind.MasterGap, Ratings(6, accuracy: 5, usefulness: 5));   // 전부 기각으로 심긴다

        var result = Engine(store).Tune();

        Assert.True(result.Changed);
        Assert.True(store.Current(Now).RuleFor(ProposalKind.MasterGap)!.MinScore < before);
        Assert.Contains(result.Changes, c => c.Reason.Contains("종합평가"));
    }

    [Fact]
    public void LowRatings_RaiseTheBar_AndRecordWhy()
    {
        var store = new ProposalStore();
        var before = store.Current(Now).RuleFor(ProposalKind.Rework)!.MinScore;
        SeedRatings(store, ProposalKind.Rework, Ratings(6, accuracy: 4, usefulness: 1));   // 기하 cbrt(4·1·3)=2.29

        var result = Engine(store).Tune();

        Assert.True(result.Changed);
        Assert.Contains(result.Changes, c => c.Field == "MinScore" && c.To > c.From);

        var after = store.Current(Now);
        Assert.True(after.RuleFor(ProposalKind.Rework)!.MinScore > before);
        Assert.Contains("종합평가", after.Rationale);
        Assert.Equal(2, store.CriteriaHistory().Count);   // 덮이지 않고 쌓인다
    }

    // 평가를 매기지 않은 결정은 학습에 들어가지 않는다.
    [Fact]
    public void DecisionsWithoutARating_DoNotMoveTheCriteria()
    {
        var store = new ProposalStore();
        var criteria = store.Current(Now);
        for (var i = 0; i < 8; i++)
        {
            var id = $"{ProposalKind.Rework}:unrated{i}";
            store.Put(new Proposal(id, ProposalKind.Rework, "t", "b",
                new ProposalEvidence(9, 3, Now, Now, Array.Empty<string>()),
                new ProposalScore(0.7, Array.Empty<ScoreDimension>()),
                ProposalStatus.Proposed, Now, criteria.Version));
            store.Decide(id, ProposalStatus.Rejected, "hong", rating: null, note: null, at: Now);
        }

        var result = Engine(store).Tune();

        Assert.False(result.Changed);
        Assert.Contains(result.Notes, n => n.Contains("rework") && n.Contains("평가 0건"));
    }

    // 정확성이 낮으면 문턱을 올려도 소용없다 — 없는 현상을 말하는 것 중 점수 높은 것이 남는다.
    // 한 숫자로 뭉쳐 있었다면 문턱만 한 칸 올리고 끝났을 자리다.
    [Fact]
    public void LowAccuracy_TurnsTheKindOffAtOnce_WithoutClimbingTheThreshold()
    {
        var store = new ProposalStore();
        var before = store.Current(Now).RuleFor(ProposalKind.Rework)!.MinScore;
        SeedRatings(store, ProposalKind.Rework, Ratings(6, accuracy: 1, usefulness: 4));

        var result = Engine(store).Tune();

        var rule = store.Current(Now).RuleFor(ProposalKind.Rework)!;
        Assert.False(rule.Enabled);
        Assert.Contains("없는 현상을 말한다", rule.DisabledReason);
        Assert.Equal(before, rule.MinScore);   // 문턱을 올리는 우회로를 거치지 않는다
        Assert.Contains(result.Changes, c => c.Reason.Contains("정확성"));
    }

    // 종합평가는 기하평균이라 실행 가능성 0이 전체를 0으로 만든다 — 문턱은 올라간다.
    // 다만 원인이 실행 가능성뿐이면 <b>끄지는 않는다</b>: 끄면 조직 사정이 풀렸을 때
    // 그 사실을 알 방법이 사라진다.
    [Fact]
    public void ActionabilityZero_RaisesTheBar_ButNeverDisablesTheKind()
    {
        var store = new ProposalStore();
        var engine = Engine(store);
        var before = store.Current(Now).RuleFor(ProposalKind.MasterGap)!.MinScore;

        for (var round = 0; round < 10; round++)
        {
            SeedRatings(store, ProposalKind.MasterGap,
                Ratings(6, accuracy: 5, usefulness: 5, actionability: 0));
            engine.Tune();
        }

        var rule = store.Current(Now).RuleFor(ProposalKind.MasterGap)!;
        Assert.True(rule.MinScore > before);   // 억제는 걸린다
        Assert.True(rule.Enabled);             // 그러나 꺼지지 않는다

        var last = engine.Tune();
        Assert.Contains(last.Notes,
            n => n.Contains("끄지 않는다") && n.Contains("실행 가능성"));
    }

    // 문턱을 끝까지 올렸는데도 낮게 평가된다면 점수가 아니라 종류가 틀렸다.
    [Fact]
    public void WhenRaisingTheBarStopsHelping_TheKindIsTurnedOffWithAReason()
    {
        var store = new ProposalStore();
        var engine = Engine(store);

        for (var round = 0; round < 8; round++)
        {
            SeedRatings(store, ProposalKind.Rework, Ratings(6, accuracy: 4, usefulness: 1));
            engine.Tune();
        }

        var rule = store.Current(Now).RuleFor(ProposalKind.Rework)!;
        Assert.False(rule.Enabled);
        Assert.Contains("종합평가", rule.DisabledReason);   // 이유 없이 꺼진 규칙은 되살릴 근거도 없다
    }

    // 조정 구간 안이면 흔들지 않는다 — 매번 움직이면 기준이 잡음을 따라간다.
    [Fact]
    public void MiddlingRatings_AreLeftAlone()
    {
        var store = new ProposalStore();
        var before = store.Current(Now).RuleFor(ProposalKind.ScreenSplit)!.MinScore;
        SeedRatings(store, ProposalKind.ScreenSplit, Ratings(6, accuracy: 4, usefulness: 3));

        var result = Engine(store).Tune();

        Assert.Equal(before, store.Current(Now).RuleFor(ProposalKind.ScreenSplit)!.MinScore);
        Assert.Contains(result.Notes, n => n.Contains("조정 구간"));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 축 가중치 — 평점과 같이 움직인 축을 올린다. 어느 축이 유용성을 예측하는지는
// 시드에서 추측한 값이었고, 사람의 평가가 그 추측을 관측으로 바꾼다.
// ─────────────────────────────────────────────────────────────────────────────
public class ProposalWeightTuningTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    private static ProposalEngine Engine(ProposalStore store) => new(
        store, new ObservationStore(), new UepStore(),
        new FoundationReconciler(new EntityStore(), new FoundationStore(Array.Empty<IFoundationSource>())),
        new DecisionLog(), () => Now);

    /// <summary>근거 축만 평점과 같이 움직이게 심는다. 나머지 축은 상수라 상관을 말할 수 없다.</summary>
    private static void SeedCorrelated(ProposalStore store, int count, bool positive)
    {
        var criteria = store.Current(Now);
        for (var i = 0; i < count; i++)
        {
            var axis = i / (double)(count - 1);                  // 0 → 1
            var usefulness = positive ? (int)(axis * 5) : 5 - (int)(axis * 5);
            var id = $"{ProposalKind.ScreenSplit}:c{i}";
            store.Put(new Proposal(id, ProposalKind.ScreenSplit, "t", "b",
                new ProposalEvidence(9, 3, Now, Now, Array.Empty<string>()),
                new ProposalScore(0.7, new[]
                {
                    new ScoreDimension("근거", axis, criteria.EvidenceWeight, ""),
                    new ScoreDimension("도달", 0.5, criteria.ReachWeight, ""),
                    new ScoreDimension("최근성", 0.5, criteria.RecencyWeight, ""),
                    new ScoreDimension("영향", 0.5, criteria.ImpactWeight, ""),
                }),
                ProposalStatus.Proposed, Now, criteria.Version));
            store.Decide(id, ProposalStatus.Accepted, "hong",
                new ProposalRating(4, usefulness, 3), null, Now);
        }
    }

    [Fact]
    public void AnAxisThatTracksTheRating_GainsWeight()
    {
        var store = new ProposalStore();
        var before = store.Current(Now);
        SeedCorrelated(store, 10, positive: true);

        var result = Engine(store).Tune();
        var after = store.Current(Now);

        Assert.Contains(result.Changes, c => c.Field.Contains("근거") && c.To > c.From);
        Assert.True(after.EvidenceWeight > before.EvidenceWeight);

        // 상수인 축은 상관을 말할 수 없다 — 0으로 세어 내리면 안 된다.
        Assert.Contains(result.Notes, n => n.Contains("전부 같아"));

        // 눈금이 회차마다 달라지면 총점을 비교할 수 없다.
        Assert.Equal(1.0, after.EvidenceWeight + after.ReachWeight + after.RecencyWeight + after.ImpactWeight, 2);
    }

    [Fact]
    public void AnAxisThatRunsAgainstTheRating_LosesWeight()
    {
        var store = new ProposalStore();
        var before = store.Current(Now);
        SeedCorrelated(store, 10, positive: false);

        Engine(store).Tune();

        Assert.True(store.Current(Now).EvidenceWeight < before.EvidenceWeight);
    }

    // 가중치는 모든 종류에 걸리는 변경이라 문턱보다 표본 하한이 높다.
    [Fact]
    public void TooFewRatings_LeaveTheWeightsAlone()
    {
        var store = new ProposalStore();
        var before = store.Current(Now);
        SeedCorrelated(store, 6, positive: true);

        var result = Engine(store).Tune();

        Assert.Equal(before.EvidenceWeight, store.Current(Now).EvidenceWeight);
        Assert.Contains(result.Notes, n => n.Contains("가중치") && n.Contains("모든 종류에 걸리는"));
    }

    [Fact]
    public void Correlation_IsUndefined_WhenEitherSideIsConstant()
    {
        Assert.Null(ProposalScoring.Correlation(new[] { 0.5, 0.5, 0.5 }, new[] { 1.0, 3.0, 5.0 }));
        Assert.Null(ProposalScoring.Correlation(new[] { 0.1, 0.5, 0.9 }, new[] { 3.0, 3.0, 3.0 }));
        Assert.Null(ProposalScoring.Correlation(new[] { 0.1, 0.9 }, new[] { 1.0, 5.0 }));   // 표본 부족

        var r = ProposalScoring.Correlation(new[] { 0.1, 0.5, 0.9 }, new[] { 1.0, 3.0, 5.0 });
        Assert.NotNull(r);
        Assert.Equal(1.0, r!.Value, 3);
    }
}
