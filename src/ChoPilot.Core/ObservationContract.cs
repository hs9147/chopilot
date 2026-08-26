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
    public const int MaxIdentifierLength = 128;
    public const int MaxRoleLength = 128;
    public const int MaxMetadataLength = 2_048;
    public const int MaxValueLength = 16_384;
    public const int MaxMaskedRefs = 20_000;
    public const long MaxAggregateCharacters = 4 * 1024 * 1024;

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
        if (!ValidIdentifier(evt.EventId)) return $"eventId가 비어 있거나 {MaxIdentifierLength}자/안전 문자 제한을 위반한다";
        if (!ValidIdentifier(evt.SessionId)) return $"sessionId가 비어 있거나 {MaxIdentifierLength}자/안전 문자 제한을 위반한다";
        if (string.IsNullOrWhiteSpace(evt.UserId) || evt.UserId.Length > MaxIdentifierLength)
            return $"userId가 비어 있거나 {MaxIdentifierLength}자를 넘는다";
        if (evt.Screen is null) return "screen이 없다";
        if (evt.Privacy is null) return "privacy가 없다";
        if (evt.Privacy.MaskedRefs is null) return "privacy.maskedRefs가 없다 (빈 배열로 보내라)";
        if (string.IsNullOrWhiteSpace(evt.Privacy.PolicyVersion) ||
            evt.Privacy.PolicyVersion.Length > MaxIdentifierLength)
            return "privacy.policyVersion이 비어 있거나 너무 길다";
        if (evt.Privacy.MaskedRefs.Count > MaxMaskedRefs)
            return $"privacy.maskedRefs가 {MaxMaskedRefs}개를 넘는다";
        if (evt.Tree is null) return "tree가 없다";
        if (TooLong(evt.Screen.Url) || TooLong(evt.Screen.Title) ||
            TooLong(evt.Screen.RecordHint?.Source) || TooLong(evt.Screen.RecordHint?.Key) ||
            TooLong(evt.Screen.RecordHint?.Value))
            return $"screen 메타데이터가 {MaxMetadataLength}자를 넘는다";

        // 오타 난 트리거를 통과시키면 완료 신호가 조용히 사라진다 —
        // "save_click"은 완료로 세어지지 않고, 아무도 그 사실을 알지 못한다.
        if (!ObservationTrigger.IsValid(evt.Trigger))
            return $"trigger '{evt.Trigger}'는 {ObservationTrigger.FocusChanged}|" +
                   $"{ObservationTrigger.StructureChanged}|{ObservationTrigger.SaveClicked} 중 하나여야 한다";

        return ValidateTree(evt.Tree, evt.Privacy.MaskedRefs);
    }

    /// <summary>
    /// 트리를 <b>반복문으로</b> 훑는다. 재귀로 검증하면 깊이 제한을 지키려는 검증기가
    /// 정작 그 깊이에서 먼저 스택을 넘긴다 — 막으려던 것에 당하는 셈이다.
    /// </summary>
    private static string? ValidateTree(UiNode root, IReadOnlyList<string> maskedRefs)
    {
        var stack = new Stack<(UiNode? Node, int Depth)>();
        stack.Push((root, 1));
        var nodes = 0;
        long characters = 0;
        var refs = new HashSet<string>(StringComparer.Ordinal);

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();

            // children 배열에 null이 섞여 올 수 있다 — 역참조 전에 막는다.
            if (node is null) return "tree에 null 노드가 있다";
            if (++nodes > MaxNodes) return $"노드가 {MaxNodes}개를 넘는다";
            if (depth > MaxDepth) return $"트리 깊이가 {MaxDepth}를 넘는다";
            if (!ValidIdentifier(node.Ref)) return $"노드의 ref가 비어 있거나 {MaxIdentifierLength}자/안전 문자 제한을 위반한다";
            if (!refs.Add(node.Ref)) return $"노드 ref '{node.Ref}'가 중복된다";
            if (string.IsNullOrWhiteSpace(node.Role) || node.Role.Length > MaxRoleLength)
                return $"노드 '{node.Ref}'의 role이 비어 있거나 {MaxRoleLength}자를 넘는다";
            if (node.Name?.Length > MaxMetadataLength)
                return $"노드 '{node.Ref}'의 name이 {MaxMetadataLength}자를 넘는다";
            if (node.AutomationId?.Length > MaxMetadataLength)
                return $"노드 '{node.Ref}'의 automationId가 {MaxMetadataLength}자를 넘는다";
            if (node.Value?.Length > MaxValueLength)
                return $"노드 '{node.Ref}'의 value가 {MaxValueLength}자를 넘는다";
            if (node.Children is null) return $"노드 '{node.Ref}'의 children이 없다 (빈 배열로 보내라)";

            characters += node.Ref.Length + node.Role.Length +
                          (node.Name?.Length ?? 0) + (node.Value?.Length ?? 0) +
                          (node.AutomationId?.Length ?? 0);
            if (characters > MaxAggregateCharacters)
                return $"트리 문자열 합계가 {MaxAggregateCharacters}자를 넘는다";

            foreach (var child in node.Children) stack.Push((child, depth + 1));
        }

        foreach (var masked in maskedRefs)
            if (string.IsNullOrWhiteSpace(masked) || masked.Length > MaxMetadataLength)
                return "privacy.maskedRefs에 비어 있거나 너무 긴 항목이 있다";

        return null;
    }

    private static bool TooLong(string? value) => value?.Length > MaxMetadataLength;

    private static bool ValidIdentifier(string? value) =>
        value is { Length: > 0 } &&
        value.Length <= MaxIdentifierLength &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/');
}
