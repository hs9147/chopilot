using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ChoPilot.Tests;

public class PrivacyEnvelopeTests
{
    [Fact]
    public void Apply_MasksMetadataAndNormalizesUrl()
    {
        var gate = new PrivacyGate();
        var screen = new ScreenInfo(
            "https://proc.example/path?q=person@example.com#private",
            "담당자 person@example.com",
            new RecordHint("url", "contact", "010-1234-5678"));
        var tree = new UiNode("n1", "Edit", "담당자", "person@example.com", "txtContact", new());

        var (safe, safeTree, masked) = gate.Apply(screen, tree);

        Assert.Equal("https://proc.example/path", safe.Url);
        Assert.Equal(PrivacyGate.MaskToken, safe.Title);
        Assert.Equal(PrivacyGate.MaskToken, safe.RecordHint!.Value);
        Assert.Equal(PrivacyGate.MaskToken, safeTree.Value);
        Assert.Empty(gate.ScanResidual(safe, safeTree));
        Assert.Contains("$screen.title", masked);
        Assert.Contains("$screen.record.value", masked);
    }

    [Fact]
    public void Contract_RejectsDuplicateRefsAndOversizedValues()
    {
        var duplicate = Event(new UiNode("root", "Form", null, null, null,
            new() { new UiNode("same", "Edit", null, null, null, new()), new UiNode("same", "Edit", null, null, null, new()) }));
        Assert.Contains("중복", ObservationContract.Validate(duplicate));

        var tooLong = Event(new UiNode("root", "Edit", null,
            new string('x', ObservationContract.MaxValueLength + 1), null, new()));
        Assert.Contains("value", ObservationContract.Validate(tooLong));
    }

    private static ObservationEvent Event(UiNode tree) => new(
        Guid.NewGuid().ToString(), "session-1", "alice", DateTimeOffset.UtcNow,
        new ScreenInfo("https://proc/path", "화면", null), tree, new PrivacyInfo("1.0", new()));
}

public class MappingConcurrencyTests
{
    private sealed class CountingMapper : IAiMapper
    {
        public int Calls;
        public async Task<MappingInference> InferAsync(
            string businessHint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(30, ct);
            return new MappingInference("PurchaseRequest",
                new() { new FieldMapping("n1", "Material", .9, "ai", false) });
        }
    }

    private sealed class BlockingMapper : IAiMapper
    {
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MappingInference> InferAsync(
            string businessHint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task;
            return new MappingInference("PurchaseRequest",
                new() { new FieldMapping("n1", "Material", .9, "ai", false) });
        }
    }

    [Fact]
    public async Task ConcurrentSameSignature_UsesOneInference()
    {
        var mapper = new CountingMapper();
        var resolver = new MappingResolver(new InMemoryMappingCache(), mapper);
        var tree = new UiNode("n1", "Edit", "품목코드", "M-001", "txtMaterial", new());
        var screen = new ScreenInfo("https://proc/pr", "구매요청", null);
        var knowledge = KnowledgeSeed.Compile();
        var signature = SignatureService.Compute(screen, tree);

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => resolver.ResolveAsync(signature, "alice", screen, tree, knowledge, "PurchaseRequest")));

        Assert.Equal(1, mapper.Calls);
        Assert.All(results, result => Assert.Equal("PurchaseRequest", result.Entry.BusinessObject));
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelSharedInference()
    {
        var mapper = new BlockingMapper();
        var resolver = new MappingResolver(new InMemoryMappingCache(), mapper);
        var tree = new UiNode("n1", "Edit", "품목코드", "M-001", "txtMaterial", new());
        var screen = new ScreenInfo("https://proc/pr", "구매요청", null);
        var knowledge = KnowledgeSeed.Compile();
        var signature = SignatureService.Compute(screen, tree);
        using var cancelled = new CancellationTokenSource();

        var first = resolver.ResolveAsync(signature, "alice", screen, tree, knowledge,
            "PurchaseRequest", cancelled.Token);
        await mapper.Started.Task;
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var joined = resolver.ResolveAsync(signature, "alice", screen, tree, knowledge, "PurchaseRequest");
        mapper.Release.TrySetResult();
        Assert.Equal("PurchaseRequest", (await joined).Entry.BusinessObject);
        Assert.Equal(1, mapper.Calls);
    }
}

public class SpoolSafetyTests
{
    [Fact]
    public async Task Spool_UsesMonotonicOrder_AndQuarantinesWhenQuotaIsExceeded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopilot-spool-safety-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new EventSpool(dir, new EventSpoolOptions(MaxEvents: 2, MaxBytes: 1_000_000));
            spool.Enqueue(Event("first"));
            spool.Enqueue(Event("second"));
            spool.Enqueue(Event("third"));

            Assert.Equal(2, spool.Status.Pending);
            Assert.Equal(1, spool.Status.Quarantined);

            var delivered = new List<string>();
            await spool.DrainAsync(e =>
            {
                delivered.Add(e.EventId);
                return Task.FromResult(true);
            });
            Assert.Equal(new[] { "second", "third" }, delivered);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Spool_CanEvictMoreThanOneOldEvent_ForALargerIncomingEvent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chopilot-spool-capacity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spool = new EventSpool(dir, new EventSpoolOptions(MaxEvents: 10, MaxBytes: 3_500));
            spool.Enqueue(Event("small-1", "x"));
            spool.Enqueue(Event("small-2", "x"));
            spool.Enqueue(Event("large", new string('x', 3_000)));

            Assert.Equal(1, spool.Status.Pending);
            Assert.Equal(2, spool.Status.Quarantined);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static ObservationEvent Event(string eventId, string value = "") => new(
        eventId, "session", "alice", DateTimeOffset.UtcNow,
        new ScreenInfo("https://proc/path", "화면", null),
        new UiNode("n1", "Form", null, value, null, new()),
        new PrivacyInfo("1.0", new()));
}

public class SecureIngestionAndFeedbackApiTests
{
    private static WebApplicationFactory<Program> Server() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("Auth:Mode", "jwt"),
                new KeyValuePair<string, string?>("Auth:Jwt:SigningKey", TestTokens.Key),
                new KeyValuePair<string, string?>("Auth:Jwt:Issuer", TestTokens.Issuer),
                new KeyValuePair<string, string?>("Auth:Jwt:Audience", TestTokens.Audience),
            })));

    private static HttpClient Client(WebApplicationFactory<Program> server, string user, params string[] roles)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Issue(user, roles: roles));
        return client;
    }

    private static ObservationEvent Event(string user, string id) => new(
        id, "session-1", user, DateTimeOffset.UtcNow,
        new ScreenInfo("https://proc/pr/create", "구매요청", null),
        new UiNode("root", "Form", null, null, null, new()
        {
            new("n1", "Edit", "품목코드", "M-001", "txtMaterial", new()),
            new("n2", "Edit", "수량", "10", "txtQuantity", new()),
        }),
        new PrivacyInfo("1.0", new()));

    [Fact]
    public async Task JwtIngestion_RejectsBodyPrincipalMismatch_AndDeduplicatesReplay()
    {
        using var server = Server();
        var alice = Client(server, "alice", ChoPilotRole.EndUser, ChoPilotRole.OpsAuditor);
        var mismatch = await alice.PostAsJsonAsync("/v1/observations", Event("bob", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);

        var id = Guid.NewGuid().ToString();
        var first = await alice.PostAsJsonAsync("/v1/observations", Event("alice", id));
        var replay = await alice.PostAsJsonAsync("/v1/observations", Event("alice", id));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayBody.RootElement.GetProperty("replayed").GetBoolean());
        var metrics = await alice.GetFromJsonAsync<JsonElement>("/v1/metrics");
        Assert.Equal(1, metrics.GetProperty("observations").GetInt32());
    }

    [Fact]
    public async Task JwtIngestion_RejectsResidualMetadataPii()
    {
        using var server = Server();
        var alice = Client(server, "alice", ChoPilotRole.EndUser);
        var evt = Event("alice", Guid.NewGuid().ToString()) with
        {
            Screen = new ScreenInfo("https://proc/path", "person@example.com", null)
        };

        var response = await alice.PostAsJsonAsync("/v1/observations", evt);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Feedback_AppliesPersonalCorrection_AndOrgRequiresAnotherReviewer()
    {
        using var server = Server();
        var alice = Client(server, "alice", ChoPilotRole.EndUser);
        var eventId = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/v1/observations", Event("alice", eventId))).StatusCode);

        var tasks = await alice.GetFromJsonAsync<JsonElement>("/v1/me/review-tasks");
        var task = Assert.Single(tasks.GetProperty("tasks").EnumerateArray());
        var element = task.GetProperty("fields")[0].GetProperty("elementKey").GetString()!;
        var revision = task.GetProperty("mappingRevision").GetInt32();
        var knowledge = task.GetProperty("knowledgeVersion").GetInt32();

        var personal = Command(eventId, element, revision, knowledge, "personal");
        var applied = await alice.PostAsJsonAsync("/v1/feedback", personal);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        using var appliedBody = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());
        Assert.Equal(FeedbackStatus.AppliedPersonal, appliedBody.RootElement.GetProperty("status").GetString());

        var org = Command(eventId, element, revision, knowledge, "org");
        var queued = await alice.PostAsJsonAsync("/v1/feedback", org);
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        using var queuedBody = JsonDocument.Parse(await queued.Content.ReadAsStringAsync());
        var reviewId = queuedBody.RootElement.GetProperty("reviewId").GetString()!;

        var own = await alice.PostAsJsonAsync("/v1/reviews/" + reviewId + "/decision",
            new FeedbackReviewDecision(true));
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);

        var bob = Client(server, "bob", ChoPilotRole.Reviewer);
        var approved = await bob.PostAsJsonAsync("/v1/reviews/" + reviewId + "/decision",
            new FeedbackReviewDecision(true));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var approvalBody = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        Assert.Equal(FeedbackStatus.ApprovedOrg, approvalBody.RootElement.GetProperty("status").GetString());
    }

    private static FeedbackCommand Command(
        string observationId, string element, int revision, int knowledgeVersion, string scope) => new(
        Guid.NewGuid().ToString(), observationId, new FeedbackTarget("mapping", element),
        FeedbackDecision.Correct, "wrong_concept", new FeedbackProposal("Quantity"), scope,
        new FeedbackExpected(revision, knowledgeVersion));
}
