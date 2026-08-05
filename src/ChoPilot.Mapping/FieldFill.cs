using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>
/// 화면에 <b>있는</b> 개념과 그중 <b>채워진</b> 개념을 가른다.
///
/// <para>
/// 가이드(빈칸 힌트)와 완료 신호 집계가 같은 정의를 써야 한다. 다르면 가이드가 "남았다"고
/// 말한 필드가 집계에서는 "채워졌다"로 세어지고, 그 모순 위에서 필수 필드 규칙이 개정된다.
/// </para>
/// </summary>
public static class FieldFill
{
    /// <summary>이 화면에서 매핑된 개념 전부 — 채움 여부의 <b>분모</b>다.</summary>
    public static string[] Observed(MappingEntry entry) =>
        entry.Mapping.Select(m => m.Concept).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 값이 있는 개념. <b>마스킹된 민감 필드는 채움으로 센다</b> —
    /// <c>mask</c>는 값을 <c>***MASKED***</c>로 바꿀 뿐 지우지 않으므로 사용자는 실제로 입력했다.
    /// 이걸 빈칸으로 세면 단가 같은 민감 필드가 영원히 "안 채워진" 것으로 보여
    /// 필수 필드 규칙에서 빠지고, 가이드는 채워진 칸을 계속 채우라고 한다.
    ///
    /// <para>
    /// <c>block</c>(주민번호 등)은 값이 null이라 빈칸으로 남는다 — 구매 업무객체의 필수 필드가
    /// 아니라 지금은 문제가 되지 않지만, 그런 개념이 필수가 되면 여기가 먼저 틀린다.
    /// </para>
    /// </summary>
    public static HashSet<string> Filled(MappingEntry entry, UiNode tree)
    {
        var byRef = new Dictionary<string, UiNode>(StringComparer.Ordinal);
        Index(tree, byRef);

        return entry.Mapping
            .Where(m => byRef.TryGetValue(m.ElementRef, out var node) && !string.IsNullOrEmpty(node.Value))
            .Select(m => m.Concept)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void Index(UiNode node, Dictionary<string, UiNode> map)
    {
        map[node.Ref] = node;
        foreach (var child in node.Children) Index(child, map);
    }
}
