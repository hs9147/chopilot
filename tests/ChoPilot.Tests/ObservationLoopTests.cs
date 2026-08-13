using ChoPilot.Core;
using Xunit;

namespace ChoPilot.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 반복 관측 (ARCHITECTURE §11).
//
// 루프가 Core에 있는 이유가 이 파일이다. ChoPilot.Client는 net8.0-windows라 여기서
// 컴파일되지 않으므로, 간격·건너뜀·오류·취소 같은 실제 판단이 클라이언트에 남아 있으면
// Windows CI가 "빌드된다"고 말해 줄 뿐 <b>맞게 도는지</b>는 아무도 확인하지 못한다.
// ─────────────────────────────────────────────────────────────────────────────

public class ChangeDetectorTests
{
    private static ScreenInfo Screen(string url = "https://proc/pr/create") => new(url, "구매요청 등록", null);

    private static UiNode Tree(string? quantity, string material = "M-001") =>
        new("n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "품목코드", material, "txtMat", new()),
            new("n3", "Edit", "수량", quantity, "txtQty", new()),
        });

    [Fact]
    public void FirstObservation_IsAlwaysAChange()
    {
        Assert.True(new ChangeDetector().HasChanged(Screen(), Tree("10")));
    }

    [Fact]
    public void IdenticalScreen_IsNotAChange()
    {
        var detector = new ChangeDetector();
        detector.HasChanged(Screen(), Tree("10"));

        // 이게 없으면 10초 루프가 아무 일도 없는 화면을 계속 올려
        // 감사 로그와 적중률 분모를 부풀린다.
        Assert.False(detector.HasChanged(Screen(), Tree("10")));
    }

    [Fact]
    public void ValueChange_IsAChange_EvenWhenTheSignatureIsNot()
    {
        // 서명은 값을 빼고 구조만 본다("같은 화면인가" = 캐시 키).
        // 지문은 값을 본다("무슨 일이 일어났는가") — 수량을 고친 것은 사건이다.
        var before = Tree("10");
        var after = Tree("20");
        Assert.Equal(SignatureService.Compute(Screen(), before), SignatureService.Compute(Screen(), after));

        var detector = new ChangeDetector();
        detector.HasChanged(Screen(), before);
        Assert.True(detector.HasChanged(Screen(), after));
    }

    [Fact]
    public void EmptyingAField_IsAChange()
    {
        var detector = new ChangeDetector();
        detector.HasChanged(Screen(), Tree("10"));
        Assert.True(detector.HasChanged(Screen(), Tree(null)));
    }

    [Fact]
    public void ScreenChange_IsAChange()
    {
        var detector = new ChangeDetector();
        detector.HasChanged(Screen(), Tree("10"));
        Assert.True(detector.HasChanged(Screen("https://proc/po/list"), Tree("10")));
    }

    [Fact]
    public void Reset_MakesTheNextObservationACHange()
    {
        // 전송이 실패하면 지문을 지운다 — 지우지 않으면 같은 화면이 "변화 없음"으로
        // 건너뛰어져 고친 뒤에도 영영 올라가지 않는다.
        var detector = new ChangeDetector();
        detector.HasChanged(Screen(), Tree("10"));
        Assert.False(detector.HasChanged(Screen(), Tree("10")));

        detector.Reset();
        Assert.True(detector.HasChanged(Screen(), Tree("10")));
    }

    [Fact]
    public void ReturningToAPreviousScreen_IsAChange()
    {
        // 직전 하나만 본다. A→B→A에서 마지막 A는 사건이다 — 화면을 오갔다는 사실이 관측이다.
        var detector = new ChangeDetector();
        detector.HasChanged(Screen(), Tree("10"));
        detector.HasChanged(Screen("https://proc/po/list"), Tree("10"));
        Assert.True(detector.HasChanged(Screen(), Tree("10")));
    }
}

public class ObservationLoopTests
{
    /// <summary>시계를 대신한다 — 간격을 실제로 기다리면 시험이 느려지기만 한다.</summary>
    private static Func<TimeSpan, CancellationToken, Task> NoWait(List<TimeSpan> recorded) =>
        (interval, ct) => { recorded.Add(interval); return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask; };

    [Fact]
    public async Task RunsTheRequestedNumberOfRounds()
    {
        var waits = new List<TimeSpan>();
        var rounds = 0;

        var stats = await new ObservationLoop(TimeSpan.FromSeconds(10),
            _ => { rounds++; return Task.FromResult(new ObservationRound(RoundOutcome.Sent)); },
            delay: NoWait(waits)).RunAsync(maxRounds: 3);

        Assert.Equal(3, rounds);
        Assert.Equal(3, stats.Rounds);
        Assert.Equal(3, stats.Of(RoundOutcome.Sent));

        // 마지막 회차 뒤에는 기다리지 않는다 — 끝났는데 간격만큼 더 붙잡고 있을 이유가 없다.
        Assert.Equal(2, waits.Count);
        Assert.All(waits, w => Assert.Equal(TimeSpan.FromSeconds(10), w));
    }

    [Fact]
    public async Task CountsEachOutcomeSeparately()
    {
        var sequence = new Queue<string>(new[]
        {
            RoundOutcome.Sent, RoundOutcome.Unchanged, RoundOutcome.Unchanged,
            RoundOutcome.Blocked, RoundOutcome.Spooled,
        });

        var stats = await new ObservationLoop(TimeSpan.Zero,
            _ => Task.FromResult(new ObservationRound(sequence.Dequeue())),
            delay: NoWait(new List<TimeSpan>())).RunAsync(maxRounds: 5);

        Assert.Equal(1, stats.Of(RoundOutcome.Sent));
        Assert.Equal(2, stats.Of(RoundOutcome.Unchanged));
        Assert.Equal(1, stats.Of(RoundOutcome.Blocked));
        Assert.Equal(1, stats.Of(RoundOutcome.Spooled));
        Assert.Contains("unchanged 2", stats.ToString());
    }

    [Fact]
    public async Task OneThrownRound_DoesNotKillTheLoop()
    {
        // UIA는 창이 닫히기만 해도 던진다. 그때마다 에이전트가 죽으면 사람이 다시
        // 눌러야 하고, 그러면 자동이 아니다.
        var rounds = 0;

        var stats = await new ObservationLoop(TimeSpan.Zero,
            _ =>
            {
                rounds++;
                if (rounds == 2) throw new InvalidOperationException("창이 닫혔다");
                return Task.FromResult(new ObservationRound(RoundOutcome.Sent));
            },
            delay: NoWait(new List<TimeSpan>())).RunAsync(maxRounds: 4);

        Assert.Equal(4, stats.Rounds);
        Assert.Equal(3, stats.Of(RoundOutcome.Sent));
        Assert.Equal(1, stats.Of(RoundOutcome.Failed));
    }

    [Fact]
    public async Task CancellationStopsTheLoop_AndKeepsTheStats()
    {
        using var cts = new CancellationTokenSource();
        var rounds = 0;

        var stats = await new ObservationLoop(TimeSpan.FromSeconds(10),
            _ =>
            {
                if (++rounds == 2) cts.Cancel();       // 회차 도중 Ctrl+C
                return Task.FromResult(new ObservationRound(RoundOutcome.Sent));
            },
            delay: NoWait(new List<TimeSpan>())).RunAsync(maxRounds: null, ct: cts.Token);

        Assert.Equal(2, stats.Rounds);                 // 취소가 통계를 지우지 않는다
        Assert.Equal(2, stats.Of(RoundOutcome.Sent));
    }

    [Fact]
    public async Task CancellationInsideARound_IsNotCountedAsFailure()
    {
        using var cts = new CancellationTokenSource();

        var stats = await new ObservationLoop(TimeSpan.Zero,
            ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(new ObservationRound(RoundOutcome.Sent)); },
            delay: NoWait(new List<TimeSpan>())).RunAsync(maxRounds: 3, ct: cts.Token);

        // Ctrl+C는 오류가 아니다 — failed로 세면 종료 요약이 매번 실패를 보고한다.
        Assert.Equal(0, stats.Rounds);
        Assert.Equal(0, stats.Of(RoundOutcome.Failed));
    }

    [Fact]
    public async Task ReportsEveryRound_InOrder()
    {
        var seen = new List<string>();

        await new ObservationLoop(TimeSpan.Zero,
            _ => Task.FromResult(new ObservationRound(RoundOutcome.Sent, "sig")),
            report: (round, result) => seen.Add($"{round}:{result.Outcome}"),
            delay: NoWait(new List<TimeSpan>())).RunAsync(maxRounds: 3);

        Assert.Equal(new[] { "1:sent", "2:sent", "3:sent" }, seen);
    }

    [Fact]
    public async Task IntervalIsMeasuredAfterTheRound_NotDuringIt()
    {
        // 회차가 간격보다 오래 걸려도 겹쳐 쏘지 않는다. 겹치면 UIA 캡처가 서로를
        // 밀어내고, 지표가 관측이 아니라 폴링 주기를 재게 된다.
        var order = new List<string>();

        await new ObservationLoop(TimeSpan.FromSeconds(5),
            async _ =>
            {
                order.Add("round-start");
                await Task.Yield();
                order.Add("round-end");
                return new ObservationRound(RoundOutcome.Sent);
            },
            delay: (_, _) => { order.Add("wait"); return Task.CompletedTask; }).RunAsync(maxRounds: 2);

        Assert.Equal(
            new[] { "round-start", "round-end", "wait", "round-start", "round-end" },
            order);
    }

    [Fact]
    public async Task NegativeInterval_IsClampedToZero()
    {
        var waits = new List<TimeSpan>();

        await new ObservationLoop(TimeSpan.FromSeconds(-5),
            _ => Task.FromResult(new ObservationRound(RoundOutcome.Sent)),
            delay: NoWait(waits)).RunAsync(maxRounds: 2);

        Assert.Equal(TimeSpan.Zero, Assert.Single(waits));
    }
}
