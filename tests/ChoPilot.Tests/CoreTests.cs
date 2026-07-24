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
