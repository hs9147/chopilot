namespace ChoPilot.Core;

/// <summary>
/// 관측 동의·범위 게이트 (ARCHITECTURE §8 "on/off 및 앱별 제외", PHASE1-DESIGN Exit #4).
/// PrivacyGate가 <b>전송 값</b>을 통제한다면, ConsentPolicy는 <b>관측 자체</b>를 통제한다.
///  - 전역 off → 관측 없음
///  - 제외 앱/URL → 해당 화면 관측 없음
/// 순수 판정(크로스플랫폼) — 단위 테스트 가능.
/// </summary>
public sealed class ConsentPolicy
{
    private readonly bool _enabled;
    private readonly string[] _excludedApps;
    private readonly string[] _excludedUrls;

    public ConsentPolicy(ConsentConfig cfg)
    {
        _enabled = cfg.Enabled;
        _excludedApps = Normalize(cfg.ExcludedApps);
        _excludedUrls = Normalize(cfg.ExcludedUrlPatterns);
    }

    public sealed record Decision(bool Allowed, string Reason);

    /// <summary>이 화면(창)을 관측·전송해도 되는지 판정.</summary>
    public Decision Evaluate(ScreenInfo screen, string? processName = null)
    {
        if (!_enabled)
            return new Decision(false, "observation disabled (consent off)");

        var title = screen.Title ?? "";
        var proc = processName ?? "";
        foreach (var app in _excludedApps)
        {
            if (Contains(title, app) || Contains(proc, app))
                return new Decision(false, $"excluded app: {app}");
        }

        var url = screen.Url ?? "";
        foreach (var pattern in _excludedUrls)
        {
            if (Contains(url, pattern))
                return new Decision(false, $"excluded url: {pattern}");
        }

        return new Decision(true, "allowed");
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string[] Normalize(IEnumerable<string>? items) =>
        (items ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToArray();
}
