using System.Security.Cryptography;
using System.Text;

namespace ChoPilot.Core;

// ─────────────────────────────────────────────────────────────────────────────
// 반복 관측 (ARCHITECTURE §11 상주 에이전트로 가는 중간 단계).
//
// chopilot-dump는 한 번 찍고 끝나는 도구였다. 측정 한 번에 사람이 한 번씩 눌러야 했고,
// 그러면 "시간에 따라 무엇이 변하는가"를 볼 수 없다.
//
// 루프 자체를 Core에 둔 것은 <b>UIA 없이 시험하기 위해서</b>다. ChoPilot.Client는
// net8.0-windows라 Linux에서 컴파일되지 않으므로, 캡처를 델리게이트로 받아 두면
// 간격·건너뜀·동의·오류·취소 같은 실제 판단이 전부 여기서 검증된다.
// 클라이언트에 남는 것은 UIA 호출 한 줄뿐이다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>관측 1회차의 결과. 무엇을 했는지가 아니라 <b>왜 그랬는지</b>까지 남긴다.</summary>
public static class RoundOutcome
{
    public const string Sent = "sent";              // 서버가 받았다
    public const string Captured = "captured";      // 캡처만 했다 (--upload 없음)
    public const string Spooled = "spooled";        // 전송 실패 — 스풀에 남았다
    public const string Rejected = "rejected";      // 서버가 영구 거부(4xx)
    public const string Unchanged = "unchanged";    // 직전과 같은 화면 — 보내지 않았다
    public const string Blocked = "blocked";        // 동의 게이트가 막았다
    public const string Failed = "failed";          // 캡처 실패·예외
}

public sealed record ObservationRound(string Outcome, string Detail = "");

/// <summary>루프 종료 시 요약. 회차별 결과 수.</summary>
public sealed record LoopStats(IReadOnlyDictionary<string, int> ByOutcome, int Rounds)
{
    public int Of(string outcome) => ByOutcome.TryGetValue(outcome, out var n) ? n : 0;

    public override string ToString() =>
        $"{Rounds}회차 — " + string.Join(" · ",
            ByOutcome.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}"));
}

/// <summary>
/// 직전 관측과 같은 화면인지 판정.
///
/// <para>
/// <see cref="SignatureService"/>와 <b>일부러 다른 것을 본다</b>. 서명은 값을 빼고 구조만 해싱한다 —
/// "같은 화면인가"(캐시 키)를 묻기 때문이다. 여기서 묻는 것은 "무슨 일이 일어났는가"이므로
/// <b>값이 들어가야</b> 한다. 사용자가 수량을 10에서 20으로 고친 것은 같은 화면이지만
/// 다른 사건이고, 진행률·완료 증거의 원자료다.
/// </para>
/// <para>
/// 이게 없으면 10초 루프가 아무 일도 없는 화면을 계속 올려 감사 로그와 적중률 분모를
/// 부풀린다 — 적중률이 100%에 수렴하는데 그건 학습이 아니라 같은 걸 반복해 센 결과다.
/// </para>
/// </summary>
public sealed class ChangeDetector
{
    private string? _last;

    /// <summary>바뀌었으면 true. 호출할 때마다 기준이 갱신된다.</summary>
    public bool HasChanged(ScreenInfo screen, UiNode tree)
    {
        var current = Fingerprint(screen, tree);
        if (current == _last) return false;

        _last = current;
        return true;
    }

    /// <summary>강제로 다음 회차를 변화로 취급한다(전송 실패 후 재시도 등).</summary>
    public void Reset() => _last = null;

    /// <summary>화면 + 값까지 포함한 지문. 서명과 달리 값이 들어간다.</summary>
    public static string Fingerprint(ScreenInfo screen, UiNode tree)
    {
        var sb = new StringBuilder();
        sb.Append(screen.Url).Append('').Append(screen.Title).Append('');
        Append(tree, sb);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static void Append(UiNode node, StringBuilder sb)
    {
        sb.Append(node.Ref).Append('')
          .Append(node.Role).Append('')
          .Append(node.AutomationId).Append('')
          .Append(node.Name).Append('')
          .Append(node.Value).Append('');

        foreach (var child in node.Children) Append(child, sb);
    }
}

/// <summary>
/// 일정 간격으로 관측 1회차를 반복한다.
///
/// <para>
/// 한 회차가 간격보다 오래 걸려도 <b>회차를 겹쳐 쏘지 않는다</b> — 간격은 회차가 끝난 뒤부터
/// 센다. 겹치면 UIA 캡처가 서로를 밀어내고, 지표가 관측이 아니라 폴링 주기를 재게 된다.
/// (측정 콘솔의 자동 반복이 setInterval이 아니라 체이닝인 것과 같은 이유다.)
/// </para>
/// <para>
/// 한 회차의 예외는 루프를 죽이지 않는다. UIA는 창이 닫히기만 해도 던지는데, 그때마다
/// 에이전트가 죽으면 사람이 다시 눌러야 하고 그러면 자동이 아니다.
/// </para>
/// </summary>
public sealed class ObservationLoop
{
    private readonly TimeSpan _interval;
    private readonly Func<CancellationToken, Task<ObservationRound>> _round;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<int, ObservationRound>? _report;

    /// <param name="delay">테스트가 시계를 대신 넣는 자리. 기본은 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public ObservationLoop(
        TimeSpan interval,
        Func<CancellationToken, Task<ObservationRound>> round,
        Action<int, ObservationRound>? report = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _interval = interval < TimeSpan.Zero ? TimeSpan.Zero : interval;
        _round = round;
        _report = report;
        _delay = delay ?? Task.Delay;
    }

    /// <param name="maxRounds">null이면 취소될 때까지. 시험과 "N회만" 실행에 쓴다.</param>
    public async Task<LoopStats> RunAsync(int? maxRounds = null, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rounds = 0;

        while (!ct.IsCancellationRequested && (maxRounds is null || rounds < maxRounds))
        {
            ObservationRound result;
            try
            {
                result = await _round(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;                                  // Ctrl+C — 통계는 남기고 조용히 끝낸다
            }
            catch (Exception ex)
            {
                result = new ObservationRound(RoundOutcome.Failed, ex.Message);
            }

            rounds++;
            counts[result.Outcome] = counts.GetValueOrDefault(result.Outcome) + 1;
            _report?.Invoke(rounds, result);

            if (maxRounds is not null && rounds >= maxRounds) break;

            // 간격은 회차가 <b>끝난 뒤</b>부터 센다.
            try
            {
                await _delay(_interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new LoopStats(counts, rounds);
    }
}
