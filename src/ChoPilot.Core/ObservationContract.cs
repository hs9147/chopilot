using System.Diagnostics.CodeAnalysis;

namespace ChoPilot.Core;

/// <summary>
/// <see cref="ObservationEvent"/> 계약 검증 (PHASE1-DESIGN §4.1).
///
/// <para>
/// 서버가 <b>4xx로 거부</b>할 수 있게 하는 것이 요점이다. 잘못된 이벤트에 500이 나면
/// 클라이언트 스풀은 그것을 서버 장애로 읽고 순서 보존을 위해 큐 머리에 남긴다 —
/// 그 뒤의 <b>모든 관측이 영원히 서버에 도달하지 못한다</b>(<see cref="EventSpool.DrainAsync"/>).
/// 계약 위반은 장애가 아니라 거부다.
/// </para>
/// <para>
/// 클라이언트도 적재 전에 같은 검증을 쓸 수 있다 — 스풀에 들어가지 않은 쓰레기가 가장 싸다.
/// </para>
/// </summary>
public static class ObservationContract
{
    /// <summary>
    /// 트리 깊이 상한. 서명·마스킹·BO 생성이 모두 재귀라 깊은 트리는
    /// <c>StackOverflowException</c>으로 <b>프로세스를 죽인다</b>(잡을 수 없는 예외다).
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>노드 수 상한. 클라이언트 캡처 상한(<c>Observation:MaxNodes</c>)과 별개로 서버가 스스로 건다.</summary>
    public const int MaxNodes = 20_000;

    /// <summary>
    /// 유효하면 <c>true</c>. 통과하면 <paramref name="evt"/>가 non-null임이 흐름 분석에 전달돼
    /// 호출측이 검증 직후 곧바로 쓸 수 있다.
    /// </summary>
    public static bool IsValid(
        [NotNullWhen(true)] ObservationEvent? evt,
        [NotNullWhen(false)] out string? violation)
    {
        violation = Validate(evt);
        return violation is null;
    }

    /// <summary>위반 사유. 유효하면 <c>null</c>.</summary>
    public static string? Validate(ObservationEvent? evt)
    {
        if (evt is null) return "이벤트 본문이 없다";
        if (string.IsNullOrWhiteSpace(evt.EventId)) return "eventId가 비어 있다";
        if (string.IsNullOrWhiteSpace(evt.SessionId)) return "sessionId가 비어 있다";
        if (string.IsNullOrWhiteSpace(evt.UserId)) return "userId가 비어 있다";
        if (evt.Screen is null) return "screen이 없다";
        if (evt.Privacy is null) return "privacy가 없다";
        if (evt.Privacy.MaskedRefs is null) return "privacy.maskedRefs가 없다 (빈 배열로 보내라)";
        if (evt.Tree is null) return "tree가 없다";

        return ValidateTree(evt.Tree);
    }

    /// <summary>
    /// 트리를 <b>반복문으로</b> 훑는다. 재귀로 검증하면 깊이 제한을 지키려는 검증기가
    /// 정작 그 깊이에서 먼저 스택을 넘긴다 — 막으려던 것에 당하는 셈이다.
    /// </summary>
    private static string? ValidateTree(UiNode root)
    {
        var stack = new Stack<(UiNode? Node, int Depth)>();
        stack.Push((root, 1));
        var nodes = 0;

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();

            // children 배열에 null이 섞여 올 수 있다 — 역참조 전에 막는다.
            if (node is null) return "tree에 null 노드가 있다";
            if (++nodes > MaxNodes) return $"노드가 {MaxNodes}개를 넘는다";
            if (depth > MaxDepth) return $"트리 깊이가 {MaxDepth}를 넘는다";
            if (string.IsNullOrWhiteSpace(node.Ref)) return "노드의 ref가 비어 있다";
            if (string.IsNullOrWhiteSpace(node.Role)) return $"노드 '{node.Ref}'의 role이 비어 있다";
            if (node.Children is null) return $"노드 '{node.Ref}'의 children이 없다 (빈 배열로 보내라)";

            foreach (var child in node.Children) stack.Push((child, depth + 1));
        }

        return null;
    }
}
