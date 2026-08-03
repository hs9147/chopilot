using ChoPilot.Core;

namespace ChoPilot.Mapping;

public sealed record MappingInference(string BusinessObject, List<FieldMapping> Fields);

/// <summary>캐시 미스 시 트리→개념 매핑을 추론 (ARCHITECTURE §5.2 step 4).</summary>
public interface IAiMapper
{
    Task<MappingInference> InferAsync(
        string businessHint,
        UiNode tree,
        Concept[] ontology,
        CancellationToken ct = default);
}

/// <summary>
/// 비-AI 베이스라인. 노드 Name을 온톨로지 alias와 매칭한다.
/// Phase 0에서 "AI가 alias 매칭 대비 얼마나 더 정확한가"를 측정하는 대조군으로 유용.
/// </summary>
public sealed class StubAiMapper : IAiMapper
{
    public Task<MappingInference> InferAsync(
        string businessHint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
    {
        var fields = new List<FieldMapping>();
        Walk(tree);
        return Task.FromResult(new MappingInference(businessHint, fields));

        void Walk(UiNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                var label = node.Name.Trim().ToLowerInvariant();
                var concept = Array.Find(ontology, c =>
                    c.Aliases.Any(a => label.Contains(a.ToLowerInvariant())) ||
                    label.Contains(c.Name.ToLowerInvariant()));

                if (concept is not null)
                    fields.Add(new FieldMapping(node.Ref, concept.Name, 0.6, "stub", concept.Sensitive));
            }
            foreach (var child in node.Children) Walk(child);
        }
    }
}
