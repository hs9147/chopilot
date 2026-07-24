using System.Text.RegularExpressions;

namespace ChoPilot.Core;

/// <summary>
/// 전송 경계 강제 관문 (ARCHITECTURE §8, PHASE1-DESIGN §6).
/// 마스킹은 <b>클라이언트에서</b> 수행 → 원문이 서버로 나가지 않는다.
///  - block : 값 제거(null)          (예: 주민번호)
///  - mask  : "***MASKED***" 로 대체  (예: 단가, 이메일)
/// </summary>
public sealed class PrivacyGate
{
    public const string MaskToken = "***MASKED***";

    private sealed record PatternRule(string Name, Regex Regex, string Action);

    private readonly List<PatternRule> _patterns;
    private readonly HashSet<string> _sensitiveConcepts;
    public string PolicyVersion { get; }

    public PrivacyGate(Concept[]? ontology = null, string policyVersion = "1.0")
    {
        PolicyVersion = policyVersion;
        ontology ??= ProcurementOntology.Concepts;
        _sensitiveConcepts = ontology.Where(c => c.Sensitive)
                                     .SelectMany(c => c.Aliases.Append(c.Name))
                                     .Select(a => a.ToLowerInvariant())
                                     .ToHashSet();

        _patterns = new()
        {
            new("rrn",   new Regex(@"\d{6}-\d{7}"), "block"),                       // 주민등록번호
            new("email", new Regex(@"[\w.\-]+@[\w.\-]+\.\w+"), "mask"),
            new("phone", new Regex(@"01[016789]-?\d{3,4}-?\d{4}"), "mask"),
            new("card",  new Regex(@"\d{4}-\d{4}-\d{4}-\d{4}"), "mask"),
        };
    }

    /// <summary>트리를 순회하며 마스킹/차단하고, 마스킹된 노드 Ref 목록을 반환.</summary>
    public (UiNode Tree, List<string> MaskedRefs) Apply(UiNode tree)
    {
        var masked = new List<string>();
        var result = Walk(tree, masked);
        return (result, masked);
    }

    private UiNode Walk(UiNode node, List<string> masked)
    {
        var (value, action) = Evaluate(node);
        if (action is not null) masked.Add(node.Ref);

        var children = node.Children.Select(c => Walk(c, masked)).ToList();
        return node with { Value = value, Children = children };
    }

    private (string? Value, string? Action) Evaluate(UiNode node)
    {
        // 1) 개념 기반: 필드 라벨(Name)이 민감 개념이면 값 마스킹
        if (node.Name is { Length: > 0 } name &&
            _sensitiveConcepts.Contains(name.Trim().ToLowerInvariant()) &&
            !string.IsNullOrEmpty(node.Value))
        {
            return (MaskToken, "mask");
        }

        // 2) 패턴 기반: 값이 PII 패턴에 매칭
        if (!string.IsNullOrEmpty(node.Value))
        {
            foreach (var rule in _patterns)
            {
                if (rule.Regex.IsMatch(node.Value))
                    return rule.Action == "block" ? (null, "block") : (MaskToken, "mask");
            }
        }

        return (node.Value, null);
    }
}
