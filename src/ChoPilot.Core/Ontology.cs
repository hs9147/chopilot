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

    /// <summary>개념 <b>이름</b>만 정확일치로 조회. 별칭은 보지 않는다.</summary>
    public static Concept? ByName(string name) =>
        Array.Find(Concepts, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 이름 <b>또는 별칭</b>으로 조회. 사람이 입력한 개념을 해석할 때 쓴다.
    /// 사용자는 화면에 보이는 라벨("단가")로 말하지 개념명("UnitPrice")으로 말하지 않는다.
    /// 별칭을 못 찾으면 그 개념의 <c>Sensitive</c> 여부를 알 수 없게 되므로,
    /// 호출측은 null을 <b>거부</b>해야 한다 — 민감하지 않다고 단정하면 안 된다.
    /// </summary>
    public static Concept? Resolve(string? nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias)) return null;
        var needle = nameOrAlias.Trim();

        return ByName(needle)
            ?? Array.Find(Concepts, c =>
                   c.Aliases.Any(a => a.Equals(needle, StringComparison.OrdinalIgnoreCase)));
    }
}
