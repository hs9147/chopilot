using System.Security.Cryptography;
using System.Text;

namespace ChoPilot.Core;

/// <summary>
/// 화면 구조 서명 (ARCHITECTURE §5.2).
/// 값(Value)·이름(Name)이 아니라 <b>구조(role + AutomationId)</b>로 계산한다.
///  - 같은 화면의 다른 레코드 → 서명 동일 → 재추론 없음 (비용 절감)
///  - 화면 구조 변경 → 서명 변경 → 자동 재추론 (self-healing)
///
/// <para>
/// 다만 구조에는 <b>데이터에 따라 변하는 부분</b>(테이블 행 수, 행별 일련 AutomationId, 열린 탭 수)이
/// 섞여 있어 그대로 해싱하면 같은 화면이 방문마다 다른 서명을 갖는다
/// (= 매번 캐시 미스 → AI 재호출 → H3b 적중률 ≥95% 달성 불가).
/// 따라서 해싱 전에 두 가지를 정규화한다:
/// </para>
/// <list type="number">
///   <item>AutomationId의 숫자 런을 <c>#</c>로 치환 — <c>grid_row_12</c> → <c>grid_row_#</c></item>
///   <item>스켈레톤이 동일한 <b>연속 형제</b>는 1회만 기록 — 3행 테이블과 10행 테이블이 같은 서명</item>
/// </list>
/// <para>
/// 트레이드오프: 숫자로만 구분되는 서로 다른 필드(<c>ctrl001</c>/<c>ctrl002</c>)는 하나로 접힌다.
/// 서명 키에 route가 함께 들어가 다른 화면끼리 충돌할 여지는 작지만,
/// 적중률(H3b)과 오적용 사이의 균형은 Phase 0 실측(<c>/v1/metrics</c>)으로 확인해야 한다.
/// </para>
/// </summary>
public static class SignatureService
{
    public static string Compute(ScreenInfo screen, UiNode tree)
    {
        var route = NormalizeRoute(screen.Url);
        var raw = $"route:{route}|skeleton:{Skeleton(tree)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>정규화된 구조 스켈레톤(해시 이전 형태). 서명이 왜 갈렸는지 진단할 때 쓴다.</summary>
    public static string Skeleton(UiNode node)
    {
        var sb = new StringBuilder();
        AppendSkeleton(node, sb);
        return sb.ToString();
    }

    private static void AppendSkeleton(UiNode node, StringBuilder sb)
    {
        sb.Append(node.Role);
        if (!string.IsNullOrEmpty(node.AutomationId))
            sb.Append('#').Append(NormalizeAutomationId(node.AutomationId));

        if (node.Children.Count == 0) return;

        sb.Append('[');
        string? previous = null;
        foreach (var child in node.Children)
        {
            var skeleton = Skeleton(child);
            if (skeleton == previous) continue;   // 반복 행·탭은 개수와 무관하게 1회만
            sb.Append(skeleton);
            previous = skeleton;
        }
        sb.Append(']');
    }

    /// <summary>AutomationId의 연속된 숫자를 <c>#</c> 하나로 치환 (행 인덱스 흡수).</summary>
    public static string NormalizeAutomationId(string automationId)
    {
        var sb = new StringBuilder(automationId.Length);
        var inDigitRun = false;
        foreach (var c in automationId)
        {
            if (char.IsDigit(c))
            {
                if (!inDigitRun) sb.Append('#');
                inDigitRun = true;
            }
            else
            {
                sb.Append(c);
                inDigitRun = false;
            }
        }
        return sb.ToString();
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
