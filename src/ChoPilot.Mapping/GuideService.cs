using ChoPilot.Core;

namespace ChoPilot.Mapping;

public sealed record GuideHint(string Type, string Text, bool Actionable);

/// <summary>Guide 조회 응답 (PHASE1-DESIGN §4.3). 읽기 전용 — 힌트는 실행 불가(Actionable=false).</summary>
public sealed record GuideResult(
    string BusinessObject,
    string Summary,
    Dictionary<string, string?> Fields,
    int Filled,
    int Required,
    double Ratio,
    List<GuideHint> NextHints,
    double Confidence,
    string Provenance);

/// <summary>
/// 현재 업무 요약 + 진행률 + 다음작업 힌트를 도출 (PHASE1-DESIGN §2.2 GuideService).
/// Phase 1은 가이드만 — 자동화(Actionable) 없음.
/// </summary>
public static class GuideService
{
    /// <summary>업무객체별 필수 개념. 미정의 시 매핑된 개념 전체를 필수로 간주.</summary>
    private static readonly Dictionary<string, string[]> RequiredByBo = new()
    {
        ["PurchaseRequest"] = new[] { "Material", "Quantity", "DeliveryDate", "Vendor" },
        ["PurchaseOrder"] = new[] { "OrderNo", "Vendor", "TotalAmount" },
    };

    public static GuideResult Build(MappingEntry entry, UiNode tree, BusinessObject bo)
    {
        var byRef = new Dictionary<string, UiNode>();
        Index(tree, byRef);

        var required = RequiredByBo.TryGetValue(entry.BusinessObject, out var r)
            ? r
            : entry.Mapping.Select(m => m.Concept).Distinct().ToArray();

        // 값이 채워진 개념 (마스킹된 민감필드는 값이 있으므로 '채움'으로 계수, block만 제외)
        var filledConcepts = entry.Mapping
            .Where(m => byRef.TryGetValue(m.ElementRef, out var n) && !string.IsNullOrEmpty(n.Value))
            .Select(m => m.Concept)
            .ToHashSet();

        var filled = required.Count(filledConcepts.Contains);
        var req = required.Length;
        var ratio = req == 0 ? 0 : Math.Round((double)filled / req, 2);

        var hints = required
            .Where(c => !filledConcepts.Contains(c))
            .Select(c => new GuideHint("guide", $"{Label(c)} 입력이 남았습니다", Actionable: false))
            .ToList();

        var record = entry.RecordId?.Value is { Length: > 0 } v ? $" {v}" : "";
        var summary = $"{entry.BusinessObject}{record} 작업 중";

        return new GuideResult(entry.BusinessObject, summary, bo.Fields, filled, req, ratio,
            hints, bo.Confidence, bo.Provenance);
    }

    private static string Label(string concept) =>
        ProcurementOntology.ByName(concept)?.Aliases.FirstOrDefault() ?? concept;

    private static void Index(UiNode node, Dictionary<string, UiNode> map)
    {
        map[node.Ref] = node;
        foreach (var child in node.Children) Index(child, map);
    }
}
