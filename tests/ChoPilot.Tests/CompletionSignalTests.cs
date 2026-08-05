using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 작업 완료 신호 (ARCHITECTURE §11).
//
// 이것이 없으면 rule.required.{BO}는 영원히 추측이다. 시드는 "구매요청에는 품목·수량·
// 납기·거래처가 필요하다"고 말하지만 아무도 확인한 적이 없고, 가이드의 "…입력이 남았습니다"
// 제안이 전부 그 위에 서 있다.
//
// 아래 시험이 겨누는 두 가지 오류:
//   1. 화면에 없던 개념을 "안 채웠다"로 세면, 그 필드가 없는 화면 변형 하나가 규칙을 흔든다.
//   2. 마스킹된 민감 필드를 빈칸으로 세면, 단가는 영원히 "안 채워진" 것이 되어 필수에서 빠진다.
// ─────────────────────────────────────────────────────────────────────────────

public class ObservationTriggerTests
{
    [Theory]
    [InlineData(null, true)]                    // 완료 신호를 안 붙이는 클라이언트도 관측은 올린다
    [InlineData("focus_changed", true)]
    [InlineData("structure_changed", true)]
    [InlineData("save_clicked", true)]
    [InlineData("save_click", false)]           // 오타를 통과시키면 완료 신호가 조용히 사라진다
    [InlineData("submitted", false)]
    public void IsValid_AcceptsOnlyTheContractVocabulary(string? trigger, bool expected) =>
        Assert.Equal(expected, ObservationTrigger.IsValid(trigger));

    [Fact]
    public void OnlySaveClicked_IsACompletion()
    {
        Assert.True(ObservationTrigger.IsCompletion(ObservationTrigger.SaveClicked));
        Assert.False(ObservationTrigger.IsCompletion(ObservationTrigger.FocusChanged));
        Assert.False(ObservationTrigger.IsCompletion(null));
    }

    [Fact]
    public void Contract_RejectsAnUnknownTrigger()
    {
        var evt = new ObservationEvent("e1", "s", "u", DateTimeOffset.UtcNow,
            new ScreenInfo("https://p/x", "t", null), UiNode.Empty("n1"),
            new PrivacyInfo("1.0", new()), Trigger: "save_click");

        Assert.Contains("trigger", ObservationContract.Validate(evt));
    }
}

public class FieldFillTests
{
    private static MappingEntry Entry(params (string Ref, string Concept, bool Sensitive)[] fields) =>
        new("sig", "global", null, "PurchaseRequest", null,
            fields.Select(f => new FieldMapping(f.Ref, f.Concept, 0.9, "ai", f.Sensitive)).ToList(),
            0.9, "trusted");

    private static UiNode Tree(params (string Ref, string? Value)[] nodes) =>
        new("root", "Window", null, null, null,
            nodes.Select(n => new UiNode(n.Ref, "Edit", null, n.Value, null, new())).ToList());

    [Fact]
    public void Observed_IsTheMappedConcepts_NotTheFilledOnes()
    {
        var entry = Entry(("n1", "Material", false), ("n2", "Vendor", false));
        Assert.Equal(new[] { "Material", "Vendor" }, FieldFill.Observed(entry));
    }

    [Fact]
    public void Filled_CountsMaskedSensitiveValuesAsFilled()
    {
        // mask는 값을 ***MASKED***로 바꿀 뿐 지우지 않는다 — 사용자는 실제로 입력했다.
        // 빈칸으로 세면 단가가 영원히 "안 채워진" 것이 되어 필수 필드에서 빠진다.
        var entry = Entry(("n1", "UnitPrice", true), ("n2", "Vendor", false));
        var filled = FieldFill.Filled(entry, Tree(("n1", PrivacyGate.MaskToken), ("n2", null)));

        Assert.Equal(new[] { "UnitPrice" }, filled);
    }

    [Fact]
    public void Filled_ExcludesEmptyAndMissingNodes()
    {
        var entry = Entry(("n1", "Material", false), ("n2", "Vendor", false), ("n9", "OrderNo", false));
        var filled = FieldFill.Filled(entry, Tree(("n1", "M-001"), ("n2", "")));

        Assert.Equal(new[] { "Material" }, filled);   // 빈 문자열도, 트리에 없는 ref도 아니다
    }
}

public class CompletionStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static CompletionRecord Record(string user, string[] observed, string[] filled) =>
        new("PurchaseRequest", user, "sig", observed, filled, T0);

    [Fact]
    public void FillRate_DividesByObserved_NotByCompletions()
    {
        // 화면에 없던 개념을 "안 채웠다"로 세면, 그 필드가 없는 화면 변형 하나가 규칙을 흔든다.
        var store = new CompletionStore();
        store.Record(Record("alice", new[] { "Material", "Vendor" }, new[] { "Material", "Vendor" }));
        store.Record(Record("bob", new[] { "Material" }, new[] { "Material" }));   // Vendor가 없는 화면

        var vendor = Assert.Single(Assert.Single(store.Stats()).Concepts, c => c.Concept == "Vendor");
        Assert.Equal(1, vendor.Observed);
        Assert.Equal(1, vendor.Filled);
        Assert.Equal(1.0, vendor.FillRate);   // 2회 중 1회가 아니라, 있었던 1회 중 1회다
    }

    [Fact]
    public void Stats_FoldPerConcept_AndCountDistinctUsers()
    {
        var store = new CompletionStore();
        store.Record(Record("alice", new[] { "Material", "Quantity" }, new[] { "Material" }));
        store.Record(Record("bob", new[] { "Material", "Quantity" }, new[] { "Material" }));
        store.Record(Record("alice", new[] { "Material", "Quantity" }, new[] { "Material", "Quantity" }));

        var stat = Assert.Single(store.Stats());
        Assert.Equal(3, stat.Completions);
        Assert.Equal(2, stat.DistinctUsers);

        var quantity = Assert.Single(stat.Concepts, c => c.Concept == "Quantity");
        Assert.Equal(3, quantity.Observed);
        Assert.Equal(1, quantity.Filled);
        Assert.Equal(2, quantity.DistinctUsers);
    }

    [Fact]
    public void Record_IgnoresCompletionsWithNoMapping()
    {
        // 매핑이 없는 화면은 "아무 개념도 필요 없다"는 증거가 아니라 그냥 증거가 아니다.
        var store = new CompletionStore();
        store.Record(Record("alice", Array.Empty<string>(), Array.Empty<string>()));

        Assert.Equal(0, store.Count);
        Assert.Empty(store.Stats());
    }
}

public class RequiredFieldAggregationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static (AxisAggregator Aggregator, KnowledgeStore Knowledge, CompletionStore Completions) Build()
    {
        var knowledge = new KnowledgeStore();
        var completions = new CompletionStore();
        return (TestFoundation.Aggregator(new UnknownConceptLog(), new UepStore(),
            new SuggestionFeedbackStore(), knowledge, new EntityStore(),
            completions: completions), knowledge, completions);
    }

    /// <summary>시드 규칙: Material · Quantity · DeliveryDate · Vendor.</summary>
    private static readonly string[] Seed = { "Material", "Quantity", "DeliveryDate", "Vendor" };

    private static void Complete(CompletionStore store, string user, string[] filled, string[]? observed = null) =>
        store.Record(new CompletionRecord("PurchaseRequest", user, "sig",
            observed ?? Seed, filled, T0));

    private static KnowledgeDoc? Draft(AggregationResult result) =>
        result.Drafts.FirstOrDefault(d => d.Id == "rule.required.PurchaseRequest");

    [Fact]
    public void AlwaysEmptyField_IsProposedForRemoval()
    {
        var (aggregator, _, completions) = Build();
        foreach (var user in new[] { "alice", "bob", "carol" })
            Complete(completions, user, new[] { "Material", "Quantity", "Vendor" });   // DeliveryDate 항상 빈칸

        var draft = Draft(aggregator.Aggregate(T0));

        Assert.NotNull(draft);
        Assert.Equal(KnowledgeType.RequiredFields, draft.Type);
        Assert.Equal(new[] { "Material", "Quantity", "Vendor" }, draft.Required!.Concepts);
        Assert.Equal("PurchaseRequest", draft.Required.BusinessObject);
        Assert.Contains("**삭제**: DeliveryDate", draft.Body);
        Assert.Contains("| DeliveryDate | 3 | 0 | 0", draft.Body);   // 증거를 본문에 싣는다
    }

    [Fact]
    public void AlwaysFilledExtraField_IsProposedForAddition()
    {
        var (aggregator, _, completions) = Build();
        var observed = Seed.Append("OrderNo").ToArray();
        foreach (var user in new[] { "alice", "bob", "carol" })
            Complete(completions, user, observed, observed);

        var draft = Draft(aggregator.Aggregate(T0));

        Assert.NotNull(draft);
        Assert.Contains("OrderNo", draft.Required!.Concepts);
        Assert.Contains("**추가**: OrderNo", draft.Body);
    }

    [Fact]
    public void AmbiguousFillRate_LeavesTheRuleAlone()
    {
        // 50~90% 구간은 증거가 애매하다. 여기서 규칙을 흔들면 가이드가 배치마다 말을 바꾸고,
        // 그러면 사용자가 가이드를 믿지 않게 된다.
        var (aggregator, _, completions) = Build();
        Complete(completions, "alice", Seed);                                        // 전부 채움
        Complete(completions, "bob", Seed);
        Complete(completions, "carol", new[] { "Material", "Quantity", "Vendor" });   // DeliveryDate만 빔
        Complete(completions, "dave", Seed);                                         // → 3/4 = 75%

        var result = aggregator.Aggregate(T0);

        Assert.Null(Draft(result));
        Assert.Contains(result.Skipped, s => s.Contains("관측이 현재 규칙과 일치"));
    }

    [Fact]
    public void NoDraft_WhenObservationAgreesWithTheRule()
    {
        var (aggregator, _, completions) = Build();
        foreach (var user in new[] { "alice", "bob", "carol" }) Complete(completions, user, Seed);

        var result = aggregator.Aggregate(T0);

        Assert.Null(Draft(result));
        Assert.Contains(result.Skipped, s => s.Contains("관측이 현재 규칙과 일치"));
    }

    [Fact]
    public void SingleUserCompletions_DoNotChangeAnOrgRule()
    {
        var (aggregator, _, completions) = Build();
        for (var i = 0; i < 10; i++) Complete(completions, "alice", new[] { "Material" });

        var result = aggregator.Aggregate(T0);

        Assert.Null(Draft(result));
        Assert.Contains(result.Skipped, s => s.Contains("k인 게이트"));
    }

    [Fact]
    public void RarelyObservedConcept_DoesNotChangeTheRule()
    {
        // 한 번 본 개념의 채움률로 규칙을 바꾸지 않는다.
        var (aggregator, _, completions) = Build();
        foreach (var user in new[] { "alice", "bob", "carol" }) Complete(completions, user, Seed);
        Complete(completions, "dave", Seed, Seed.Append("TotalAmount").ToArray());   // TotalAmount 1회만

        var result = aggregator.Aggregate(T0);

        Assert.Null(Draft(result));   // TotalAmount는 증거가 1건뿐이라 추가되지 않는다
    }

    [Fact]
    public void PendingRevision_IsNotStacked()
    {
        var (aggregator, knowledge, completions) = Build();
        foreach (var user in new[] { "alice", "bob", "carol" })
            Complete(completions, user, new[] { "Material", "Quantity", "Vendor" });

        var first = Draft(aggregator.Aggregate(T0))!;
        knowledge.Submit(first, "aggregator", T0);

        var second = aggregator.Aggregate(T0);
        Assert.Null(Draft(second));
        Assert.Contains(second.Skipped, s => s.Contains("검수 대기 중"));
    }

    [Fact]
    public void ApprovedRevision_ChangesTheCompiledRule()
    {
        var (aggregator, knowledge, completions) = Build();
        Assert.Equal(Seed, knowledge.Current.RequiredFor("PurchaseRequest"));

        foreach (var user in new[] { "alice", "bob", "carol" })
            Complete(completions, user, new[] { "Material", "Quantity", "Vendor" });

        var draft = Draft(aggregator.Aggregate(T0))!;
        knowledge.Submit(draft, "aggregator", T0);
        var (published, error) = knowledge.Approve(draft.Id, "kim", T0);

        Assert.Null(error);
        Assert.Equal(2, published!.Version);   // 시드 v1 → 개정 v2
        Assert.Equal(new[] { "Material", "Quantity", "Vendor" },
            knowledge.Current.RequiredFor("PurchaseRequest"));
    }
}

public class CompletionApiTests
{
    private static ObservationEvent Event(string user, string? vendor, string? trigger) => new(
        EventId: Guid.NewGuid().ToString(), SessionId: "s", UserId: user,
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo("https://proc/pr/create", "구매요청 등록", null),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "품목코드", "M-001", "txtMat", new()),
            new("n3", "Edit", "거래처", vendor, "txtVendor", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new()),
        Trigger: trigger);

    [Fact]
    public async Task OnlyCompletionEvents_AreRecorded()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        await client.PostAsJsonAsync("/v1/observations", Event("alice", "A사", ObservationTrigger.FocusChanged));
        await client.PostAsJsonAsync("/v1/observations", Event("alice", "A사", null));
        await client.PostAsJsonAsync("/v1/observations", Event("alice", "A사", ObservationTrigger.SaveClicked));

        var completions = await client.GetFromJsonAsync<JsonElement>("/v1/completions");

        // 작성 중간의 화면은 증거가 아니다 — 빈칸이 미완인지 불필요인지 구분되지 않는다.
        Assert.Equal(1, completions.GetProperty("count").GetInt32());
        var bo = Assert.Single(completions.GetProperty("businessObjects").EnumerateArray());
        Assert.Equal("PurchaseRequest", bo.GetProperty("businessObject").GetString());
    }

    [Fact]
    public async Task UnknownTrigger_Is400()
    {
        using var server = new WebApplicationFactory<Program>();
        var response = await server.CreateClient()
            .PostAsJsonAsync("/v1/observations", Event("alice", "A사", "save_click"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CompletionStats_ExposeFillRatePerConcept()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        // 거래처를 비운 채 저장한 완료가 섞인다
        await client.PostAsJsonAsync("/v1/observations", Event("alice", "A사", ObservationTrigger.SaveClicked));
        await client.PostAsJsonAsync("/v1/observations", Event("bob", null, ObservationTrigger.SaveClicked));

        var completions = await client.GetFromJsonAsync<JsonElement>("/v1/completions");
        var bo = Assert.Single(completions.GetProperty("businessObjects").EnumerateArray());

        Assert.Equal(2, bo.GetProperty("completions").GetInt32());
        Assert.Equal(2, bo.GetProperty("distinctUsers").GetInt32());

        var vendor = Assert.Single(bo.GetProperty("concepts").EnumerateArray(),
            c => c.GetProperty("concept").GetString() == "Vendor");
        Assert.Equal(2, vendor.GetProperty("observed").GetInt32());
        Assert.Equal(1, vendor.GetProperty("filled").GetInt32());
        Assert.Equal(0.5, vendor.GetProperty("fillRate").GetDouble());
    }
}
