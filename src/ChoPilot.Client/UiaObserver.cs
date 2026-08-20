using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using ChoPilot.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace ChoPilot.Client;

/// <summary>
/// 브라우저 접근성 트리 관측 (PHASE1-DESIGN §2.1 WebObserver).
/// 포그라운드 창(Win32 GetForegroundWindow)을 UIA로 열어 정규화 트리로 직렬화한다.
/// Chrome/Edge는 Extension 없이 UIA 접근성 트리를 노출한다(ARCHITECTURE D3).
///
/// FlaUI 호출은 COM 예외가 잦으므로 프로퍼티는 ValueOrDefault(비예외)로,
/// 나머지는 try/catch로 감싼다.
/// </summary>
public sealed class UiaObserver : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private readonly UIA3Automation _automation = new();
    public (ScreenInfo Screen, UiNode Tree, string? ProcessName) CaptureForegroundWindow(int maxDepth = 25, int maxNodes = 4000)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("포그라운드 창을 찾지 못했습니다.");

        var window = _automation.FromHandle(hwnd);

        var count = 0;
        var tree = Serialize(window, "node-root", 0, maxDepth, maxNodes, ref count);

        var url = TryGetUrl(window);
        var title = Prop(() => window.Properties.Name.ValueOrDefault);
        var screen = new ScreenInfo(
            Url: url,
            Title: title,
            RecordHint: ScreenIdentifier.Identify(url, title)); // H2 레코드 식별

        GetWindowThreadProcessId(hwnd, out var pid);
        string? process = null;
        try { process = Process.GetProcessById((int)pid).ProcessName; }
        catch { /* window may have closed */ }
        return (screen, tree, process);
    }

    private UiNode Serialize(
        AutomationElement element, string stableRef, int depth, int maxDepth, int maxNodes, ref int count)
    {
        var automationId = NullIfEmpty(Prop(() => element.Properties.AutomationId.ValueOrDefault));
        var role = Prop(() => element.Properties.ControlType.ValueOrDefault.ToString()) ?? "Unknown";
        var node = new UiNode(
            Ref: stableRef,
            Role: role,
            Name: NullIfEmpty(Prop(() => element.Properties.Name.ValueOrDefault)),
            Value: NullIfEmpty(ReadValue(element)),
            AutomationId: automationId,
            Children: new List<UiNode>());

        count++;
        if (depth >= maxDepth || count >= maxNodes) return node;

        var childIndex = 0;
        foreach (var child in SafeChildren(element))
        {
            if (count >= maxNodes) break;
            childIndex++;
            var childId = NullIfEmpty(Prop(() => child.Properties.AutomationId.ValueOrDefault));
            var childRole = Prop(() => child.Properties.ControlType.ValueOrDefault.ToString()) ?? "Unknown";
            var segment = childId is { Length: > 0 }
                ? "id-" + StableToken(childId)
                : "role-" + StableToken(childRole) + "-" + childIndex;
            node.Children.Add(Serialize(child, StableRef(stableRef, segment),
                depth + 1, maxDepth, maxNodes, ref count));
        }
        return node;
    }

    private static AutomationElement[] SafeChildren(AutomationElement e)
    {
        try { return e.FindAllChildren(); }
        catch { return Array.Empty<AutomationElement>(); }
    }

    private static string? ReadValue(AutomationElement e)
    {
        try { return e.Patterns.Value.PatternOrDefault?.Value?.ValueOrDefault; }
        catch { return null; }
    }

    /// <summary>
    /// 주소창 식별자가 확인되는 Edit만 URL로 수집한다. 첫 Edit는 폼 입력/비밀번호일 수 있어
    /// URL로 취급하면 Privacy Gate 바깥의 개인정보를 화면 메타데이터로 승격시키는 결함이다.
    /// </summary>
    private static string? TryGetUrl(AutomationElement window)
    {
        try
        {
            foreach (var edit in window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
            {
                var automationId = Prop(() => edit.Properties.AutomationId.ValueOrDefault) ?? "";
                var name = Prop(() => edit.Properties.Name.ValueOrDefault) ?? "";
                if (!IsAddressBar(automationId, name)) continue;

                var value = edit.Patterns.Value.PatternOrDefault?.Value?.ValueOrDefault;
                return PrivacyGate.NormalizeUrl(NullIfEmpty(value));
            }
            return null;
        }
        catch { return null; }
    }

    private static bool IsAddressBar(string automationId, string name)
    {
        var candidate = (automationId + " " + name).ToLowerInvariant();
        return candidate.Contains("omnibox") ||
               candidate.Contains("address") ||
               candidate.Contains("urlbar") ||
               candidate.Contains("주소창");
    }

    private static string StableToken(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-');
        return sb.ToString().Trim('-') is { Length: > 0 } token ? token : "unknown";
    }

    private static string StableRef(string parent, string segment) =>
        "node-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parent + "/" + segment)))[..20]
            .ToLowerInvariant();

    private static T? Prop<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose() => _automation.Dispose();
}
