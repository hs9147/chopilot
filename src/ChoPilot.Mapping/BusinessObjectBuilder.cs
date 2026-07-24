using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>매핑 엔트리를 트리에 적용해 Business Object 생성. 민감 필드는 값 제외.</summary>
public static class BusinessObjectBuilder
{
    public static BusinessObject Build(MappingEntry entry, UiNode tree)
    {
        var byRef = new Dictionary<string, UiNode>();
        Index(tree, byRef);

        var fields = new Dictionary<string, string?>();
        foreach (var m in entry.Mapping)
        {
            var value = byRef.TryGetValue(m.ElementRef, out var node) ? node.Value : null;
            // 민감 필드는 값을 담지 않는다(존재만 표시). PrivacyGate에서 이미 마스킹되었어도 방어적 처리.
            fields[m.Concept] = m.Sensitive ? null : value;
        }

        return new BusinessObject(entry.BusinessObject, fields, entry.Confidence,
            entry.Mapping.FirstOrDefault()?.Provenance ?? "cache");
    }

    private static void Index(UiNode node, Dictionary<string, UiNode> map)
    {
        map[node.Ref] = node;
        foreach (var child in node.Children) Index(child, map);
    }
}
