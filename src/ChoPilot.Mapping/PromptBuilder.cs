using System.Text;
using System.Text.Json;
using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>Bedrock 매핑 추론 프롬프트 생성 (순수 함수 — 단위 테스트 가능).</summary>
public static class PromptBuilder
{
    public const int MaxPromptNodes = 2_000;
    public const int MaxPromptCharacters = 120_000;
    public static string System(Concept[] ontology)
    {
        var concepts = ontology.Select(c => new
        {
            name = c.Name, type = c.Type, aliases = c.Aliases, sensitive = c.Sensitive
        });

        return $$"""
        You map enterprise web-app UI elements to business concepts.
        Given an accessibility tree and an ontology, return ONLY JSON:
        {"business_object": string, "fields": [{"element_ref": string, "concept": string, "confidence": number}], "unmapped": [string]}
        Rules: use only concept names from the ontology; confidence in [0,1]; do not invent element_refs.
        Ontology:
        {{JsonSerializer.Serialize(concepts)}}
        """;
    }

    public static string User(string businessHint, UiNode tree)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"business_hint: {businessHint}");
        sb.AppendLine("accessibility_tree (ref | role | name):");
        var remaining = MaxPromptNodes;
        Flatten(tree, sb, ref remaining);
        return sb.ToString();
    }

    private static void Flatten(UiNode node, StringBuilder sb, ref int remaining)
    {
        if (remaining-- <= 0 || sb.Length >= MaxPromptCharacters) return;
        // 값(Value)은 프롬프트에 넣지 않는다 — 구조·라벨만으로 매핑 (PII 최소화).
        if (node.Role is "Edit" or "ComboBox" or "Text" or "DataItem" or "Table" || node.Name is { Length: > 0 })
        {
            var name = node.Name is { Length: > 512 } longName ? longName[..512] : node.Name;
            sb.AppendLine($"  {node.Ref} | {node.Role} | {name}");
        }

        foreach (var child in node.Children)
        {
            if (remaining <= 0 || sb.Length >= MaxPromptCharacters) break;
            Flatten(child, sb, ref remaining);
        }
    }
}
