namespace ChoPilot.Core;

public sealed record Concept(
    string Name,
    string Type,               // "string" | "number" | "date"
    string[] Aliases,
    bool Sensitive = false,
    string? EntityRef = null); // KG 연결 힌트 (예: "Company")

/// <summary>
/// Core Ontology (ARCHITECTURE §5.1 Layer A) — 고정 자산.
/// Adaptive Semantic Mapping의 개념 사전이자 AI few-shot 시드의 기반.
/// </summary>
public static class ProcurementOntology
{
    public static readonly Concept[] Concepts =
    {
        new("Material",     "string", new[] { "품목", "품목코드", "자재", "자재코드", "item" }),
        new("Quantity",     "number", new[] { "수량", "발주수량", "요청수량", "qty" }),
        new("UnitPrice",    "number", new[] { "단가", "공급단가", "unit price" }, Sensitive: true),
        new("TotalAmount",  "number", new[] { "금액", "합계", "총액", "amount" }, Sensitive: true),
        new("Vendor",       "string", new[] { "거래처", "협력사", "공급업체", "vendor" }, EntityRef: "Company"),
        new("DeliveryDate", "date",   new[] { "납기", "납품일", "delivery" }),
        new("RequestNo",    "string", new[] { "요청번호", "구매요청번호", "pr no" }),
        new("OrderNo",      "string", new[] { "발주번호", "po no" }),
    };

    public static Concept? ByName(string name) =>
        Array.Find(Concepts, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
