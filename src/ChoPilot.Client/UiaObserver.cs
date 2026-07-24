using System.Runtime.InteropServices;
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

    private readonly UIA3Automation _automation = new();
    private int _refCounter;

    public (ScreenInfo Screen, UiNode Tree) CaptureForegroundWindow(int maxDepth = 25, int maxNodes = 4000)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("포그라운드 창을 찾지 못했습니다.");

        var window = _automation.FromHandle(hwnd);

        _refCounter = 0;
        var count = 0;
        var tree = Serialize(window, 0, maxDepth, maxNodes, ref count);

        var screen = new ScreenInfo(
            Url: TryGetUrl(window),
            Title: Prop(() => window.Properties.Name.ValueOrDefault),
            RecordHint: null); // 레코드 식별(URL 파싱)은 ScreenIdentifier 단계

        return (screen, tree);
    }

    private UiNode Serialize(AutomationElement element, int depth, int maxDepth, int maxNodes, ref int count)
    {
        var node = new UiNode(
            Ref: $"n{++_refCounter}",
            Role: Prop(() => element.Properties.ControlType.ValueOrDefault.ToString()) ?? "Unknown",
            Name: NullIfEmpty(Prop(() => element.Properties.Name.ValueOrDefault)),
            Value: NullIfEmpty(ReadValue(element)),
            AutomationId: NullIfEmpty(Prop(() => element.Properties.AutomationId.ValueOrDefault)),
            Children: new List<UiNode>());

        count++;
        if (depth >= maxDepth || count >= maxNodes) return node;

        foreach (var child in SafeChildren(element))
        {
            if (count >= maxNodes) break;
            node.Children.Add(Serialize(child, depth + 1, maxDepth, maxNodes, ref count));
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

    /// <summary>브라우저 주소창(첫 Edit)에서 URL 추정. 앱/브라우저별 보정 필요.</summary>
    private static string? TryGetUrl(AutomationElement window)
    {
        try
        {
            var edit = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));
            var value = edit?.Patterns.Value.PatternOrDefault?.Value?.ValueOrDefault;
            return NullIfEmpty(value);
        }
        catch { return null; }
    }

    private static T? Prop<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose() => _automation.Dispose();
}
