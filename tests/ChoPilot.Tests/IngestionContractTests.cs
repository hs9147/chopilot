using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 적재 계약과 영구 거부.
//
// 이 두 가지는 한 결함의 양면이다. 잘못된 이벤트에 서버가 500을 내면 클라이언트 스풀은
// 그것을 <b>서버 장애</b>로 읽고, FIFO를 지키느라 큐 머리에 남긴다 — 그 뒤의 모든 관측이
// 영원히 서버에 도달하지 못한다. 파일이 멀쩡하므로 손상 파일 격리에도 걸리지 않는다.
//
// 그래서 서버는 4xx로 거부해야 하고(계약 위반은 장애가 아니다), 스풀은 4xx를 재시도가
// 아니라 격리로 처리해야 한다. 어느 한쪽만 고치면 다른 쪽이 여전히 큐를 막는다.
// ─────────────────────────────────────────────────────────────────────────────

public class ObservationContractTests
{
    private static UiNode Node(string @ref, params UiNode[] children) =>
        new(@ref, "Edit", null, null, null, children.ToList());

    private static ObservationEvent Valid(UiNode? tree = null) => new(
        EventId: "e1", SessionId: "s", UserId: "u",
        CapturedAt: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        Screen: new ScreenInfo("https://proc/pr/create", "구매요청 등록", null),
        Tree: tree ?? Node("n1", Node("n2")),
        Privacy: new PrivacyInfo("1.0", new()));

    [Fact]
    public void Accepts_AWellFormedEvent() => Assert.Null(ObservationContract.Validate(Valid()));

    [Fact]
    public void IsValid_NarrowsTheEventForCallers()
    {
        // 검증 직후 곧바로 쓸 수 있어야 한다 — 아니면 호출측이 다시 null 검사를 넣는다.
        Assert.True(ObservationContract.IsValid(Valid(), out var violation));
        Assert.Null(violation);
    }

    [Theory]
    [InlineData(null, "이벤트 본문이 없다")]
    public void Rejects_NullEvent(ObservationEvent? evt, string expected) =>
        Assert.Equal(expected, ObservationContract.Validate(evt));

    [Fact]
    public void Rejects_EmptyIdentifiers()
    {
        Assert.Contains("eventId", ObservationContract.Validate(Valid() with { EventId = " " }));
        Assert.Contains("sessionId", ObservationContract.Validate(Valid() with { SessionId = "" }));
        Assert.Contains("userId", ObservationContract.Validate(Valid() with { UserId = "" }));
    }

    [Fact]
    public void Rejects_MissingRequiredSections()
    {
        Assert.Contains("screen", ObservationContract.Validate(Valid() with { Screen = null! }));
        Assert.Contains("tree", ObservationContract.Validate(Valid() with { Tree = null! }));
        Assert.Contains("privacy", ObservationContract.Validate(Valid() with { Privacy = null! }));
    }

    [Fact]
    public void Rejects_NullMaskedRefs()
    {
        // 실제로 밟은 결함: maskedRefs 누락 → AuditService에서 NullReferenceException → 500.
        var violation = ObservationContract.Validate(
            Valid() with { Privacy = new PrivacyInfo("1.0", null!) });

        Assert.Contains("maskedRefs", violation);
    }

    [Fact]
    public void Rejects_NullChildrenAndNullNodes()
    {
        // 실제로 밟은 결함: children 누락 → BusinessObjectBuilder.Index에서 터진다.
        Assert.Contains("children", ObservationContract.Validate(
            Valid(new UiNode("n1", "Form", null, null, null, null!))));

        var withNullChild = new UiNode("n1", "Form", null, null, null, new List<UiNode> { null! });
        Assert.Contains("null 노드", ObservationContract.Validate(Valid(withNullChild)));
    }

    [Fact]
    public void Rejects_EmptyRefOrRole()
    {
        Assert.Contains("ref", ObservationContract.Validate(
            Valid(new UiNode(" ", "Form", null, null, null, new()))));
        Assert.Contains("role", ObservationContract.Validate(
            Valid(new UiNode("n1", "", null, null, null, new()))));
    }

    [Fact]
    public void Rejects_TreeDeeperThanTheLimit()
    {
        // 서명·마스킹·BO 생성이 모두 재귀다. StackOverflowException은 잡을 수 없고
        // 프로세스를 죽인다 — 적재 엔드포인트에서 그것은 곧 가용성 결함이다.
        var deep = Node("leaf");
        for (var i = 0; i < ObservationContract.MaxDepth + 10; i++) deep = Node($"n{i}", deep);

        Assert.Contains("깊이", ObservationContract.Validate(Valid(deep)));
    }

    [Fact]
    public void Validator_ItselfSurvivesADeepTree()
    {
        // 재귀로 검증하면 깊이 제한을 지키려는 검증기가 정작 그 깊이에서 먼저 스택을 넘는다.
        var deep = Node("leaf");
        for (var i = 0; i < 100_000; i++) deep = Node($"n{i}", deep);

        Assert.NotNull(ObservationContract.Validate(Valid(deep)));   // 죽지 않고 사유를 돌려준다
    }

    [Fact]
    public void Rejects_TooManyNodes()
    {
        var children = Enumerable.Range(0, ObservationContract.MaxNodes + 1)
            .Select(i => Node($"c{i}")).ToArray();

        Assert.Contains("노드가", ObservationContract.Validate(Valid(Node("root", children))));
    }
}

public class SendOutcomeClassificationTests
{
    private static ServerResponse Response(int? status, bool success) =>
        new(success, status, "");

    [Theory]
    [InlineData(200, true, SendOutcome.Sent)]
    [InlineData(400, false, SendOutcome.Rejected)]   // 계약 위반 — 다시 보내도 같다
    [InlineData(404, false, SendOutcome.Rejected)]
    [InlineData(408, false, SendOutcome.Retry)]      // 타임아웃은 시간이 지나면 성공할 수 있다
    [InlineData(429, false, SendOutcome.Retry)]      // 요청 과다도 마찬가지
    [InlineData(500, false, SendOutcome.Retry)]
    [InlineData(503, false, SendOutcome.Retry)]
    [InlineData(null, false, SendOutcome.Retry)]     // 네트워크 예외 — 상태 코드가 없다
    public void Classify_SeparatesPermanentFromTransient(int? status, bool success, SendOutcome expected) =>
        Assert.Equal(expected, Uploader.Classify(Response(status, success)));
}

public class SpoolRejectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RejectedEvent_IsQuarantined_AndDoesNotBlockTheQueue()
    {
        var dir = TestEvents.TempDir();
        try
        {
            var spool = new EventSpool(dir);
            spool.Enqueue(TestEvents.Evt("bad", T0));
            spool.Enqueue(TestEvents.Evt("good-1", T0.AddSeconds(1)));
            spool.Enqueue(TestEvents.Evt("good-2", T0.AddSeconds(2)));

            var attempted = new List<string>();
            var drain = await spool.DrainAsync(e =>
            {
                attempted.Add(e.EventId);
                return Task.FromResult(e.EventId == "bad" ? SendOutcome.Rejected : SendOutcome.Sent);
            });

            // 거부를 재시도로 취급했다면 여기서 "bad" 하나만 시도하고 멈췄을 것이다.
            Assert.Equal(new[] { "bad", "good-1", "good-2" }, attempted);
            Assert.Equal(2, drain.Sent);
            Assert.Equal(1, drain.Rejected);
            Assert.Equal(0, spool.PendingCount);
            Assert.Single(Directory.GetFiles(dir, "*.bad"));   // 유실이 아니라 격리다
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RetryableFailure_StillStopsTheDrain_ToPreserveOrder()
    {
        var dir = TestEvents.TempDir();
        try
        {
            var spool = new EventSpool(dir);
            spool.Enqueue(TestEvents.Evt("a", T0));
            spool.Enqueue(TestEvents.Evt("b", T0.AddSeconds(1)));

            var drain = await spool.DrainAsync(_ => Task.FromResult(SendOutcome.Retry));

            Assert.Equal(0, drain.Sent);
            Assert.Equal(0, drain.Rejected);
            Assert.Equal(2, spool.PendingCount);
            Assert.Empty(Directory.GetFiles(dir, "*.bad"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RejectedCurrentEvent_IsNotSpooled()
    {
        var dir = TestEvents.TempDir();
        try
        {
            var spool = new EventSpool(dir);
            var result = await ObservationDispatcher.DispatchAsync(
                spool, TestEvents.Evt("bad", T0), _ => Task.FromResult(SendOutcome.Rejected));

            Assert.False(result.Sent);
            Assert.Equal(SendOutcome.Rejected, result.Outcome);
            // 적재하면 다음 실행에서 큐 머리를 막는다 — 지금 버리는 편이 낫다.
            Assert.Equal(0, spool.PendingCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RetryableCurrentEvent_IsStillSpooled()
    {
        var dir = TestEvents.TempDir();
        try
        {
            var spool = new EventSpool(dir);
            var result = await ObservationDispatcher.DispatchAsync(
                spool, TestEvents.Evt("a", T0), _ => Task.FromResult(SendOutcome.Retry));

            Assert.False(result.Sent);
            Assert.Equal(1, spool.PendingCount);   // 유실 방지는 그대로다
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public class IngestionApiTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<HttpResponseMessage> Post(HttpClient client, string body) =>
        await client.PostAsync("/v1/observations",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

    [Fact]
    public async Task MalformedEvent_Is400_Not500()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        // privacy.maskedRefs 누락 · tree.children 누락 — 둘 다 예전에는 500이었다.
        var missingMaskedRefs = """
            {"eventId":"e1","sessionId":"s","userId":"u","capturedAt":"2026-08-01T00:00:00Z",
             "screen":{"url":"https://proc/pr/create","title":"구매요청 등록"},
             "tree":{"ref":"n1","role":"Form","children":[]},
             "privacy":{"policyVersion":"1.0"}}
            """;
        var missingChildren = """
            {"eventId":"e1","sessionId":"s","userId":"u","capturedAt":"2026-08-01T00:00:00Z",
             "screen":{"url":"https://proc/pr/create","title":"구매요청 등록"},
             "tree":{"ref":"n1","role":"Form"},
             "privacy":{"policyVersion":"1.0","maskedRefs":[]}}
            """;

        foreach (var body in new[] { missingMaskedRefs, missingChildren })
        {
            var response = await Post(client, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("invalid observation event", doc.RootElement.GetProperty("error").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("detail").GetString()));
        }

        // 거부된 이벤트는 적재되지 않는다 — 반쪽 관측이 지표를 오염시키면 안 된다.
        var observations = await client.GetFromJsonAsync<JsonElement>("/v1/observations");
        Assert.Equal(0, observations.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task WellFormedEvent_StillSucceeds()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/observations", TestEvents.Evt(), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
