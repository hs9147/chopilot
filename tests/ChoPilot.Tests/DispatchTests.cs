using System.Net;
using ChoPilot.Core;
using Xunit;

namespace ChoPilot.Tests;

internal static class TestEvents
{
    public static ObservationEvent Evt(string id = "e1", DateTimeOffset? at = null) => new(
        EventId: id, SessionId: "s", UserId: "u",
        CapturedAt: at ?? new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        Screen: new ScreenInfo("https://proc/pr/create", "구매요청 등록", null),
        Tree: new UiNode("n1", "Form", null, null, null, new()),
        Privacy: new PrivacyInfo("1.0", new()));

    public static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "chopilot-dispatch-test-" + Guid.NewGuid().ToString("N"));
}

/// <summary>지정한 응답을 돌려주거나 예외를 던지는 스텁 핸들러.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode? _status;
    private readonly Exception? _throw;

    public StubHandler(HttpStatusCode status) => _status = status;
    public StubHandler(Exception ex) => _throw = ex;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_throw is not null) throw _throw;
        return Task.FromResult(new HttpResponseMessage(_status!.Value) { Content = new StringContent("{}") });
    }
}

public class UploaderTests
{
    private static Uploader For(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1") }, ownsClient: true);

    [Fact]
    public async Task Post_Succeeds_On2xx()
    {
        using var uploader = For(new StubHandler(HttpStatusCode.OK));
        var result = await uploader.PostObservationAsync(TestEvents.Evt());

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Post_Fails_On5xx_WithoutThrowing()
    {
        // 회귀 방지: 서버 5xx는 예외가 아니므로, 예외만 잡던 호출측에서 성공으로 오인돼 이벤트가 유실됐다.
        using var uploader = For(new StubHandler(HttpStatusCode.InternalServerError));
        var result = await uploader.PostObservationAsync(TestEvents.Evt());

        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);
    }

    [Fact]
    public async Task Post_Fails_OnNetworkError()
    {
        using var uploader = For(new StubHandler(new HttpRequestException("connection refused")));
        var result = await uploader.PostObservationAsync(TestEvents.Evt());

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task Post_Propagates_CallerCancellation()
    {
        using var uploader = For(new StubHandler(new TaskCanceledException()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 호출자가 취소한 경우는 "전송 실패"로 삼키지 않고 전파한다.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => uploader.PostObservationAsync(TestEvents.Evt(), cts.Token));
    }
}

public class ObservationDispatcherTests
{
    [Fact]
    public async Task Spools_Event_WhenSendFails()
    {
        var dir = TestEvents.TempDir();
        try
        {
            var spool = new EventSpool(dir);
            var result = await ObservationDispatcher.DispatchAsync(
                spool, TestEvents.Evt(), _ => Task.FromResult(false));

            Assert.False(result.Sent);
            Assert.Equal(1, result.Pending);       // 유실되지 않고 스풀에 남는다
            Assert.Equal(1, spool.PendingCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Spools_Event_WhenSenderThrows()
    {
        var dir = TestEvents.TempDir();
        try
        {
            var spool = new EventSpool(dir);
            var result = await ObservationDispatcher.DispatchAsync(
                spool, TestEvents.Evt(), _ => throw new HttpRequestException("down"));

            Assert.False(result.Sent);
            Assert.Equal(1, spool.PendingCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task KeepsSpoolEmpty_WhenSendSucceeds()
    {
        var dir = TestEvents.TempDir();
        try
        {
            var spool = new EventSpool(dir);
            var result = await ObservationDispatcher.DispatchAsync(
                spool, TestEvents.Evt(), _ => Task.FromResult(true));

            Assert.True(result.Sent);
            Assert.Equal(0, spool.PendingCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task PreservesFifo_ByNotJumpingAheadOfPendingSpool()
    {
        var dir = TestEvents.TempDir();
        try
        {
            var t0 = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            var spool = new EventSpool(dir);
            spool.Enqueue(TestEvents.Evt("old-1", t0));
            spool.Enqueue(TestEvents.Evt("old-2", t0.AddSeconds(1)));

            var attempts = new List<string>();
            var result = await ObservationDispatcher.DispatchAsync(
                spool, TestEvents.Evt("current", t0.AddSeconds(2)),
                e => { attempts.Add(e.EventId); return Task.FromResult(false); });

            // 밀린 스풀 재전송이 실패하면 현재 이벤트는 전송 시도 없이 스풀 뒤로 간다.
            Assert.Equal(new[] { "old-1" }, attempts);
            Assert.False(result.Sent);
            Assert.Equal(3, spool.PendingCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Drains_Backlog_ThenSendsCurrent_WhenServerRecovers()
    {
        var dir = TestEvents.TempDir();
        try
        {
            var t0 = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            var spool = new EventSpool(dir);
            spool.Enqueue(TestEvents.Evt("old-1", t0));
            spool.Enqueue(TestEvents.Evt("old-2", t0.AddSeconds(1)));

            var order = new List<string>();
            var result = await ObservationDispatcher.DispatchAsync(
                spool, TestEvents.Evt("current", t0.AddSeconds(2)),
                e => { order.Add(e.EventId); return Task.FromResult(true); });

            Assert.Equal(new[] { "old-1", "old-2", "current" }, order);   // 오래된 순 → 현재
            Assert.Equal(2, result.Drained);
            Assert.True(result.Sent);
            Assert.Equal(0, spool.PendingCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
