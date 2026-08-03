using System.Security.Cryptography;
using System.Text;

namespace ChoPilot.Core;

/// <summary>
/// 화면 구조 서명 (ARCHITECTURE §5.2).
/// 값(Value)·이름(Name)이 아니라 <b>구조(role + AutomationId)</b>로 계산한다.
///  - 같은 화면의 다른 레코드 → 서명 동일 → 재추론 없음 (비용 절감)
///  - 화면 구조 변경 → 서명 변경 → 자동 재추론 (self-healing)
/// </summary>
public static class SignatureService
{
    public static string Compute(ScreenInfo screen, UiNode tree)
    {
        var route = NormalizeRoute(screen.Url);
        var skeleton = new StringBuilder();
        AppendSkeleton(tree, skeleton);

        var raw = $"route:{route}|skeleton:{skeleton}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AppendSkeleton(UiNode node, StringBuilder sb)
    {
        sb.Append(node.Role);
        if (!string.IsNullOrEmpty(node.AutomationId))
            sb.Append('#').Append(node.AutomationId);

        if (node.Children.Count == 0) return;

        sb.Append('[');
        foreach (var child in node.Children)
            AppendSkeleton(child, sb);
        sb.Append(']');
    }

    /// <summary>쿼리스트링(레코드 값)을 제거한 경로만 사용 → 레코드가 달라도 서명 유지.</summary>
    public static string NormalizeRoute(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : url.Split('?')[0];
    }
}
