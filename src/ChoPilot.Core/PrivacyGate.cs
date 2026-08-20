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

    /// <summary>
    /// 화면 메타데이터와 트리를 함께 통과시키는 실제 전송 경계.
    /// UIA의 Name·창 제목·URL에도 사용자 입력과 레코드 값이 들어갈 수 있으므로
    /// 값만 마스킹한 트리는 안전한 페이로드가 아니다.
    /// </summary>
    public (ScreenInfo Screen, UiNode Tree, List<string> MaskedRefs) Apply(ScreenInfo screen, UiNode tree)
    {
        var (safeTree, masked) = Apply(tree);

        var url = NormalizeUrl(screen.Url);
        var title = SanitizeMetadata(url: false, screen.Title, "$screen.title", masked);
        url = SanitizeMetadata(url: true, url, "$screen.url", masked);

        RecordHint? hint = null;
        if (screen.RecordHint is { } original)
        {
            var source = SanitizeMetadata(false, original.Source, "$screen.record.source", masked) ?? "";
            var key = SanitizeMetadata(false, original.Key, "$screen.record.key", masked) ?? "";
            var value = SanitizeMetadata(false, original.Value, "$screen.record.value", masked);
            hint = new RecordHint(source, key, value);
        }

        return (new ScreenInfo(url, title, hint), safeTree, masked);
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

        var name = SanitizeMetadata(false, node.Name, $"{node.Ref}.name", masked);
        var automationId = SanitizeMetadata(false, node.AutomationId, $"{node.Ref}.automationId", masked);
        return node with { Name = name, Value = value, AutomationId = automationId, Children = children };
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

    /// <summary>
    /// 게이트를 통과한 트리에 PII 원문이 남아 있는지 되짚는다 (H4 반증 테스트).
    /// 기대 민감필드 목록이 틀렸을 수 있으므로, 재현율과 별개로 페이로드 자체를 훑어야 한다.
    /// </summary>
    public IReadOnlyList<string> ScanResidual(UiNode tree)
    {
        var hits = new List<string>();
        Scan(tree);
        return hits;

        void Scan(UiNode node)
        {
            if (node.Value is { Length: > 0 } value && value != MaskToken &&
                _patterns.Any(rule => rule.Regex.IsMatch(value)))
                hits.Add(node.Ref);

            foreach (var child in node.Children) Scan(child);
        }
    }

    public IReadOnlyList<string> ScanResidual(ScreenInfo screen, UiNode tree)
    {
        var hits = ScanResidual(tree).ToList();
        Scan("$screen.url", screen.Url);
        Scan("$screen.title", screen.Title);
        if (screen.RecordHint is { } hint)
        {
            Scan("$screen.record.source", hint.Source);
            Scan("$screen.record.key", hint.Key);
            Scan("$screen.record.value", hint.Value);
        }
        return hits;

        void Scan(string location, string? value)
        {
            if (value is { Length: > 0 } text && text != MaskToken &&
                _patterns.Any(rule => rule.Regex.IsMatch(text)))
                hits.Add(location);
        }
    }

    private string? SanitizeMetadata(bool url, string? value, string location, List<string> masked)
    {
        if (string.IsNullOrEmpty(value)) return value;
        foreach (var rule in _patterns)
        {
            if (!rule.Regex.IsMatch(value)) continue;
            masked.Add(location);
            return rule.Action == "block" ? null : MaskToken;
        }
        return value;
    }

    /// <summary>URL은 scheme/host/path만 남긴다. query와 fragment는 토큰·검색어·레코드 원문이 되기 쉽다.</summary>
    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;

        var builder = new UriBuilder(uri) { Query = "", Fragment = "" };
        return builder.Uri.GetLeftPart(UriPartial.Path);
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
