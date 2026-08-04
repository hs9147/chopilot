using System.Text.RegularExpressions;

namespace ChoPilot.Core;

/// <summary>
/// 전송 경계 강제 관문 (ARCHITECTURE §8, PHASE1-DESIGN §6).
/// 마스킹은 <b>클라이언트에서</b> 수행 → 원문이 서버로 나가지 않는다.
///  - block : 값 제거(null)          (예: 주민번호)
///  - mask  : "***MASKED***" 로 대체  (예: 단가, 이메일)
///
/// <para>
/// 민감 판정은 세 경로를 쓴다. 웹 접근성 트리는 라벨과 값이 한 노드에 있는 경우가 오히려 드물고,
/// <c>&lt;Text name="단가"/&gt;</c> 옆에 <c>&lt;Edit name="" value="12000"/&gt;</c> 가 붙는 형태가 흔하다.
/// 자기 Name만 보면 이런 값이 그대로 서버로 나간다(H4 재현율 ≥99% 미달).
/// </para>
/// <list type="number">
///   <item>자기 <c>Name</c> 이 민감 개념</item>
///   <item>자기 <c>AutomationId</c> 에 민감 개념이 포함 (<c>txtUnitPrice</c>)</item>
///   <item><b>직전 형제가 민감 개념 라벨</b> — 값 없는 라벨 노드의 민감도를 뒤따르는 값 노드가 상속</item>
/// </list>
/// <para>
/// 판정은 과마스킹 쪽으로 기운다. 민감도를 놓치면 원문이 유출되지만, 과하게 잡으면 정확도만 떨어진다.
/// </para>
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
        var result = Walk(tree, masked, inheritedSensitive: false);
        return (result, masked);
    }

    private UiNode Walk(UiNode node, List<string> masked, bool inheritedSensitive)
    {
        var (value, action) = Evaluate(node, inheritedSensitive);
        if (action is not null) masked.Add(node.Ref);

        // 값 없는 래퍼(Group 등)가 라벨 뒤에 오면 민감도를 자식까지 흘려보낸다.
        var carry = inheritedSensitive && string.IsNullOrEmpty(node.Value);

        var children = new List<UiNode>(node.Children.Count);
        foreach (var child in node.Children)
        {
            children.Add(Walk(child, masked, carry));

            if (IsSensitiveLabel(child)) carry = true;
            else if (!string.IsNullOrEmpty(child.Value)) carry = false;  // 값 노드가 라벨을 소비
        }

        return node with { Value = value, Children = children };
    }

    /// <summary>값 없이 민감 개념만 이름으로 가진 노드 = 뒤따르는 값의 라벨.</summary>
    private bool IsSensitiveLabel(UiNode node) =>
        string.IsNullOrEmpty(node.Value) && IsSensitiveName(node.Name);

    private (string? Value, string? Action) Evaluate(UiNode node, bool inheritedSensitive)
    {
        if (string.IsNullOrEmpty(node.Value)) return (node.Value, null);

        // 1) 패턴 기반이 우선 — block(주민번호)은 mask보다 강한 조치다.
        foreach (var rule in _patterns)
        {
            if (rule.Regex.IsMatch(node.Value))
                return rule.Action == "block" ? (null, "block") : (MaskToken, "mask");
        }

        // 2) 개념 기반 — 자기 라벨 / 자동화ID / 직전 형제 라벨.
        if (inheritedSensitive || IsSensitiveName(node.Name) || IsSensitiveAutomationId(node.AutomationId))
            return (MaskToken, "mask");

        return (node.Value, null);
    }

    private bool IsSensitiveName(string? name) =>
        name is { Length: > 0 } && _sensitiveConcepts.Contains(name.Trim().ToLowerInvariant());

    /// <summary>자동화ID는 완전일치가 아니라 포함으로 본다 (<c>txtUnitPrice</c> → UnitPrice).</summary>
    private bool IsSensitiveAutomationId(string? automationId)
    {
        if (automationId is not { Length: > 0 }) return false;
        var id = automationId.ToLowerInvariant();
        return _sensitiveConcepts.Any(concept => id.Contains(concept));
    }
}
