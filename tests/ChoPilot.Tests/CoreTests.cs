using ChoPilot.Core;
using ChoPilot.Mapping;
using Xunit;

namespace ChoPilot.Tests;

public class SignatureServiceTests
{
    private static UiNode Form(string materialValue) => new(
        "n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "품목코드", materialValue, "txtMat", new()),
            new("n3", "Edit", "수량", "10", "txtQty", new()),
        });

    [Fact]
    public void Signature_IsStable_AcrossDifferentRecordValues()
    {
        var screen1 = new ScreenInfo("https://proc/pr/create?id=PR1", "구매요청", null);
        var screen2 = new ScreenInfo("https://proc/pr/create?id=PR2", "구매요청", null);

        var s1 = SignatureService.Compute(screen1, Form("M-001"));
        var s2 = SignatureService.Compute(screen2, Form("M-999"));

        // 값·쿼리스트링이 달라도 구조가 같으면 서명 동일 → 재추론 없음
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void Signature_Changes_WhenStructureChanges()
    {
        var screen = new ScreenInfo("https://proc/pr/create", "구매요청", null);
        var withExtraField = Form("M-001") with
        {
            Children = new()
            {
                new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
                new("n3", "Edit", "수량", "10", "txtQty", new()),
                new("n4", "Edit", "납기", "2026-08-01", "txtDue", new()),
            }
        };

        var s1 = SignatureService.Compute(screen, Form("M-001"));
        var s2 = SignatureService.Compute(screen, withExtraField);

        // 구조 변경 → 서명 변경 → 자가치유(재추론) 트리거
        Assert.NotEqual(s1, s2);
    }

    /// <summary>행 n개짜리 발주 목록 테이블. 행별 AutomationId에 인덱스가 들어간다.</summary>
    private static UiNode Grid(int rows) => new(
        "n1", "Window", "발주 조회", null, null, new()
        {
            new("g", "Table", "발주목록", null, "grdOrders", Enumerable.Range(1, rows).Select(i =>
                new UiNode($"r{i}", "DataItem", $"행 {i}", null, $"grdOrders_row_{i}", new()
                {
                    new($"r{i}c1", "Text", "발주번호", $"PO-{i:D4}", $"cell_{i}_no", new()),
                    new($"r{i}c2", "Text", "거래처", "A사", $"cell_{i}_vendor", new()),
                })).ToList()),
        });

    [Fact]
    public void Signature_IsStable_AcrossRowCounts()
    {
        var screen = new ScreenInfo("https://proc/po/list", "발주 조회", null);

        // 가상화 테이블은 스크롤 위치에 따라 트리에 노출되는 행 수가 달라진다.
        // 정규화가 없으면 방문마다 서명이 바뀌어 캐시가 전혀 듣지 않는다(H3b).
        Assert.Equal(
            SignatureService.Compute(screen, Grid(3)),
            SignatureService.Compute(screen, Grid(10)));
    }

    [Fact]
    public void Signature_IsStable_WhenOnlyDigitsInAutomationIdDiffer()
    {
        var screen = new ScreenInfo("https://proc/pr/create", "구매요청", null);
        UiNode WithId(string id) => new("n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", id, new()),
        });

        Assert.Equal(
            SignatureService.Compute(screen, WithId("txtMat_7")),
            SignatureService.Compute(screen, WithId("txtMat_812")));
    }

    [Fact]
    public void Signature_StillChanges_WhenColumnIsAdded()
    {
        var screen = new ScreenInfo("https://proc/po/list", "발주 조회", null);
        var withExtraColumn = Grid(3);
        foreach (var row in withExtraColumn.Children[0].Children)
            row.Children.Add(new UiNode($"{row.Ref}c3", "Text", "금액", "1000", "cell_amt", new()));

        // 행 수는 흡수하되 열 구성 변경(실제 구조 변경)은 여전히 감지해야 자가치유가 성립한다.
        Assert.NotEqual(
            SignatureService.Compute(screen, Grid(3)),
            SignatureService.Compute(screen, withExtraColumn));
    }

    [Fact]
    public void NormalizeAutomationId_ReplacesDigitRuns()
    {
        Assert.Equal("grid_row_#", SignatureService.NormalizeAutomationId("grid_row_12"));
        Assert.Equal("txtMat", SignatureService.NormalizeAutomationId("txtMat"));
        Assert.Equal("c#_#", SignatureService.NormalizeAutomationId("c10_3"));
    }
}

public class PrivacyGateTests
{
    [Fact]
    public void Masks_SensitiveConcept_ByLabel()
    {
        var tree = new UiNode("n1", "Form", null, null, null, new()
        {
            new("n2", "Edit", "단가", "12000", null, new()),   // 민감 개념
            new("n3", "Edit", "수량", "10", null, new()),
        });

        var (result, masked) = new PrivacyGate().Apply(tree);

        Assert.Contains("n2", masked);
        Assert.Equal(PrivacyGate.MaskToken, result.Children[0].Value);
        Assert.Equal("10", result.Children[1].Value); // 비민감은 유지
    }

    [Fact]
    public void Blocks_ResidentRegistrationNumber_ByPattern()
    {
        var tree = new UiNode("n1", "Text", "메모", "담당자 900101-1234567", null, new());
        var (result, masked) = new PrivacyGate().Apply(tree);

        Assert.Contains("n1", masked);
        Assert.Null(result.Value); // block → 값 제거
    }
}

public class StubAiMapperTests
{
    [Fact]
    public async Task MapsLabels_ToConcepts_ByAlias()
    {
        var tree = new UiNode("n1", "Form", null, null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", null, new()),
            new("n3", "Edit", "거래처", "A사", null, new()),
        });

        var inference = await new StubAiMapper()
            .InferAsync("PurchaseRequest", tree, ProcurementOntology.Concepts);

        Assert.Contains(inference.Fields, f => f.Concept == "Material" && f.ElementRef == "n2");
        Assert.Contains(inference.Fields, f => f.Concept == "Vendor" && f.ElementRef == "n3");
    }
}

public class GuideServiceTests
{
    [Fact]
    public void Build_ComputesProgress_AndNextHints()
    {
        // 트리: Material·Quantity 채움, DeliveryDate 비어있음
        var tree = new UiNode("n0", "Form", null, null, null, new()
        {
            new("n1", "Edit", "품목코드", "M-001", null, new()),
            new("n2", "Edit", "수량", "10", null, new()),
            new("n3", "Edit", "납기", "", null, new()),   // 미입력
            new("n4", "Edit", "거래처", "A사", null, new()),
        });

        var entry = new MappingEntry(
            Signature: "sig", Scope: "global", UserId: null,
            BusinessObject: "PurchaseRequest", RecordId: new RecordHint("url_query", "id", "PR123"),
            Mapping: new()
            {
                new("n1", "Material", 0.95, "ai", false),
                new("n2", "Quantity", 0.95, "ai", false),
                new("n3", "DeliveryDate", 0.90, "ai", false),
                new("n4", "Vendor", 0.92, "ai", false),
            },
            Confidence: 0.93, Status: "trusted");

        var bo = BusinessObjectBuilder.Build(entry, tree);
        var guide = GuideService.Build(entry, tree, bo);

        Assert.Equal("PurchaseRequest", guide.BusinessObject);
        Assert.Contains("PR123", guide.Summary);
        Assert.Equal(4, guide.Required);
        Assert.Equal(3, guide.Filled);                 // 납기 제외
        Assert.Equal(0.75, guide.Ratio);
        Assert.Single(guide.NextHints);                // 납기 힌트 1개
        Assert.All(guide.NextHints, h => Assert.False(h.Actionable));  // Phase 1: 실행 불가
    }
}

public class BedrockParseTests
{
    [Fact]
    public void Parse_ExtractsFields_AndDropsHallucinatedConcepts()
    {
        // Bedrock(InvokeModel) 응답 형태를 모사: content[0].text 안에 JSON
        var body = """
        {"content":[{"type":"text","text":"결과: {\"business_object\":\"PurchaseRequest\",\"fields\":[{\"element_ref\":\"n2\",\"concept\":\"Material\",\"confidence\":0.95},{\"element_ref\":\"n9\",\"concept\":\"NotARealConcept\",\"confidence\":0.9}]}"}]}
        """;

        var inference = BedrockAiMapper.Parse(body, "PurchaseRequest", ProcurementOntology.Concepts);

        Assert.Equal("PurchaseRequest", inference.BusinessObject);
        Assert.Single(inference.Fields);                        // 환각 개념은 제거됨
        Assert.Equal("Material", inference.Fields[0].Concept);
        Assert.Equal("ai", inference.Fields[0].Provenance);
    }
}
