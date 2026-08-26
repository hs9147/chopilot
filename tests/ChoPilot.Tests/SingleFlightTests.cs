using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChoPilot.Tests;

public class SingleFlightTests
{
    [Fact]
    public async Task SameName_SecondCallerIsTurnedAwayWithoutRunningTheWork()
    {
        var gate = new SingleFlight();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var runs = 0;

        var first = gate.RunAsync("refresh", async () =>
        {
            Interlocked.Increment(ref runs);
            started.SetResult();
            await release.Task;
            return "done";
        });

        await started.Task;
        var second = await gate.RunAsync("refresh", () =>
        {
            Interlocked.Increment(ref runs);      // 여기 오면 안 된다 — 거절은 일이 시작되기 전이다
            return Task.FromResult("done");
        });

        Assert.Null(second);
        Assert.Equal(1, runs);
        Assert.Equal(new[] { "refresh" }, gate.Running);

        release.SetResult();
        Assert.Equal("done", await first);
        Assert.Empty(gate.Running);
    }

    [Fact]
    public async Task DifferentNames_DoNotBlockEachOther()
    {
        var gate = new SingleFlight();
        var release = new TaskCompletionSource();

        var refresh = gate.RunAsync("refresh", async () => { await release.Task; return "a"; });
        var aggregate = await gate.RunAsync("aggregate", () => Task.FromResult("b"));

        Assert.Equal("b", aggregate);
        release.SetResult();
        Assert.Equal("a", await refresh);
    }

    // 예외로 게이트가 안 풀리면 한 번 실패한 작업이 프로세스가 살아 있는 내내 막힌다.
    [Fact]
    public async Task WorkThatThrows_StillReleasesTheGate()
    {
        var gate = new SingleFlight();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync<string>("refresh", () => throw new InvalidOperationException("boom")));

        Assert.Empty(gate.Running);
        Assert.Equal("ok", await gate.RunAsync("refresh", () => Task.FromResult("ok")));
    }
}

/// <summary>테스트가 놓아줄 때까지 붙잡고 있는 편집자. 이걸로 "집계 도중"을 재현한다.</summary>
internal sealed class BlockingKnowledgeEditor : IKnowledgeEditor
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public async Task<string> DescribeAsync(KnowledgeDoc draft, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Entered.TrySetResult();
        await Release.Task;
        return draft.Body;
    }
}

/// <summary>테스트가 놓아줄 때까지 붙잡고 있는 기반 출처. "출처 갱신 도중"을 재현한다.</summary>
internal sealed class BlockingFoundationSource : IFoundationSource
{
    public string Id => "blocking";
    public string Title => "테스트용 지연 출처";
    public string Kind => FoundationKind.Currency;
    public string Origin => "test";
    public string License => "test";
    public bool RequiresNetwork => true;      // 부팅 시 즉시 조회를 피한다

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fetches;

    public int Fetches => Volatile.Read(ref _fetches);

    public async Task<FoundationFetch> FetchAsync(FoundationQuery query, CancellationToken ct)
    {
        Interlocked.Increment(ref _fetches);
        Entered.TrySetResult();
        await Release.Task;
        return new FoundationFetch(Id, Array.Empty<FoundationFact>(), null, DateTimeOffset.UtcNow);
    }
}

public class SingleFlightApiTests
{
    private static HttpRequestMessage WithUser(HttpMethod method, string url, string user, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add(RequestUser.Header, user);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    // 콘솔 버튼을 비활성화해도 탭 두 개·새로고침·직접 호출은 그대로 들어온다.
    // 무료 출처는 일일 할당량이 있으므로 중복 조회는 비용이다.
    [Fact]
    public async Task FoundationRefresh_SecondConcurrentCall_Is409_AndDoesNotRefetch()
    {
        var blocking = new BlockingFoundationSource();
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IFoundationSource>(blocking)));
        var client = server.CreateClient();

        var first = client.SendAsync(WithUser(HttpMethod.Post, "/v1/foundation/refresh", "ops"));
        await blocking.Entered.Task;

        var second = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/foundation/refresh", "ops"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using (var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync()))
            Assert.Contains("이미 실행 중", body.RootElement.GetProperty("error").GetString());

        Assert.Equal(1, blocking.Fetches);        // 두 번째는 밖으로 나가지 않았다

        blocking.Release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);

        // 끝난 뒤에는 다시 눌러진다 — 게이트지 잠금이 아니다.
        var third = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/foundation/refresh", "ops"));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(2, blocking.Fetches);
    }

    // 집계는 초안 1건당 LLM 1회다. 겹쳐 들어오면 "이미 존재" 판정이 비용보다 뒤에 있어
    // 두 번 부르고 나서야 거부된다 — 그래서 진입에서 끊는다.
    [Fact]
    public async Task KnowledgeAggregate_SecondConcurrentCall_Is409_AndDoesNotCallTheEditorTwice()
    {
        var editor = new BlockingKnowledgeEditor();
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IKnowledgeEditor>(editor)));
        var client = server.CreateClient();

        // 세 사람이 같은 미지 개념으로 정정을 시도한다 → 승격 게이트를 넘겨 초안 1건이 생긴다
        foreach (var user in new[] { "alice", "bob", "carol" })
            await client.SendAsync(WithUser(HttpMethod.Post, "/v1/correction", user,
                new CorrectionRequest("sig-x", "PurchaseRequest",
                    new List<CorrectionField> { new("n2", "결제조건") })));

        var first = client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/aggregate", "ops"));
        await editor.Entered.Task;

        var second = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/aggregate", "ops"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, editor.Calls);            // LLM은 한 번만 불렸다

        editor.Release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
    }

    // 미리보기는 쓰지도, 밖으로 호출하지도 않는다. 제출이 도는 동안에도 봐야 한다.
    [Fact]
    public async Task DryRunPreview_IsNotBlockedByARunningSubmit()
    {
        var editor = new BlockingKnowledgeEditor();
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IKnowledgeEditor>(editor)));
        var client = server.CreateClient();

        foreach (var user in new[] { "alice", "bob", "carol" })
            await client.SendAsync(WithUser(HttpMethod.Post, "/v1/correction", user,
                new CorrectionRequest("sig-x", "PurchaseRequest",
                    new List<CorrectionField> { new("n2", "결제조건") })));

        var submit = client.SendAsync(WithUser(HttpMethod.Post, "/v1/knowledge/aggregate", "ops"));
        await editor.Entered.Task;

        var preview = await client.SendAsync(
            WithUser(HttpMethod.Post, "/v1/knowledge/aggregate?dryRun=true", "ops"));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using (var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()))
            Assert.True(body.RootElement.GetProperty("dryRun").GetBoolean());

        editor.Release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await submit).StatusCode);
    }
}
