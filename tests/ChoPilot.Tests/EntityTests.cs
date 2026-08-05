using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

public class EntityResolverTests
{
    private static readonly CompiledKnowledge K = KnowledgeSeed.Compile();

    private static BusinessObject Bo(params (string Concept, string? Value)[] fields) =>
        new("PurchaseRequest", fields.ToDictionary(f => f.Concept, f => f.Value), 0.9, "ai");

    [Theory]
    [InlineData("A사", "A사")]
    [InlineData(" A사 ", "A사")]
    [InlineData("a-corp", "A-CORP")]
    [InlineData("A   사", "A 사")]
    [InlineData("Ａ사", "A사")]          // 전각 → 반각
    public void Normalize_FoldsSpacingCaseAndWidth(string raw, string expected) =>
        Assert.Equal(expected, EntityResolver.Normalize(raw));

    [Fact]
    public void Extract_TakesOnlyEntityConcepts()
    {
        var mentions = EntityResolver.Extract(
            Bo(("Material", "M-001"), ("Vendor", "A사"), ("Quantity", "10")), K);

        // Quantity는 EntityRef가 없다 — 수량은 엔티티가 아니다
        Assert.Equal(2, mentions.Count);
        Assert.Contains(mentions, m => m.Entity == new EntityKey("Item", "M-001"));
        Assert.Contains(mentions, m => m.Entity == new EntityKey("Company", "A사"));
    }

    [Fact]
    public void Extract_SkipsSensitiveConcepts_AndEmptyValues()
    {
        // UnitPrice는 민감이라 BO에 값이 오지 않지만, 와도 엔티티가 되지 않는다.
        var mentions = EntityResolver.Extract(
            Bo(("Material", null), ("Vendor", "  "), ("UnitPrice", "12000")), K);

        Assert.Empty(mentions);
    }

    [Fact]
    public void Extract_KeepsRawVariant_ForSplitDiagnosis()
    {
        var mention = Assert.Single(EntityResolver.Extract(Bo(("Vendor", " a사 ")), K));
        Assert.Equal("A사", mention.Entity.Key);
        Assert.Equal(" a사 ", mention.Raw);       // 원문 보존 — 갈림 진단의 원자료
    }
}

public class EntityStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly CompiledKnowledge K = KnowledgeSeed.Compile();

    private static IReadOnlyList<EntityMention> Mentions(string material, string vendor) =>
        EntityResolver.Extract(
            new BusinessObject("PurchaseRequest",
                new Dictionary<string, string?> { ["Material"] = material, ["Vendor"] = vendor },
                0.9, "ai"), K);

    [Fact]
    public void Record_Accumulates_MentionsUsersAndVariants()
    {
        var store = new EntityStore();
        store.Record(Mentions("M-001", "A사"), "alice", "sig1", T0);
        store.Record(Mentions("m-001", " A사"), "bob", "sig2", T0.AddMinutes(1));

        var item = Assert.Single(store.All(), e => e.Type == "Item");
        Assert.Equal("M-001", item.Key);
        Assert.Equal(2, item.Mentions);
        Assert.Equal(2, item.DistinctUsers);
        Assert.Equal(2, item.DistinctSignatures);
        Assert.Equal(new[] { "M-001", "m-001" }, item.Variants);   // 원문 변형 보존
    }

    [Fact]
    public void Links_AreOrderIndependent()
    {
        // (Item,Company)와 (Company,Item)이 갈리면 지지도가 절반으로 쪼개져 게이트를 못 넘는다.
        var store = new EntityStore();
        store.Record(Mentions("M-001", "A사"), "alice", "sig1", T0);
        store.Record(Mentions("M-001", "A사"), "bob", "sig1", T0);

        var link = Assert.Single(store.Links());
        Assert.Equal(2, link.Count);
        Assert.Equal(2, link.DistinctUsers);
    }

    [Fact]
    public void Splits_ReportsPunctuationVariants_WithoutMerging()
    {
        var store = new EntityStore();
        store.Record(Mentions("M-001", "A사"), "alice", "sig1", T0);
        store.Record(Mentions("M001", "A사"), "bob", "sig2", T0);

        var split = Assert.Single(store.Splits());
        Assert.Equal("Item", split.Type);
        Assert.Equal(new[] { "M-001", "M001" }, split.Keys);

        // 보고만 한다 — 잘못 합치면 서로 다른 품목이 하나가 되고 오류가 전파된다
        Assert.Equal(2, store.All().Count(e => e.Type == "Item"));
    }

    [Fact]
    public void Splits_IsEmpty_WhenKeysAgree()
    {
        var store = new EntityStore();
        store.Record(Mentions("M-001", "A사"), "alice", "sig1", T0);
        store.Record(Mentions(" m-001 ", "a사"), "bob", "sig1", T0);

        Assert.Empty(store.Splits());
        Assert.Equal(2, store.All().Count);   // 정규화로 접혀 Item 1 + Company 1
    }
}

public class ItemAxisAggregationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly CompiledKnowledge K = KnowledgeSeed.Compile();

    private static void Observe(EntityStore store, string user, string material, string vendor) =>
        store.Record(EntityResolver.Extract(
            new BusinessObject("PurchaseRequest",
                new Dictionary<string, string?> { ["Material"] = material, ["Vendor"] = vendor },
                0.9, "ai"), K), user, "sig", T0);

    private static AxisAggregator Aggregator(EntityStore entities, KnowledgeStore knowledge) =>
        new(new UnknownConceptLog(), new UepStore(), new SuggestionFeedbackStore(), knowledge, entities);

    [Fact]
    public void CooccurrenceBecomesItemDraft_WhenBothGatesPass()
    {
        var entities = new EntityStore();
        foreach (var user in new[] { "alice", "bob", "carol" }) Observe(entities, user, "M-001", "A사");

        var result = Aggregator(entities, new KnowledgeStore()).Aggregate(T0);

        var draft = Assert.Single(result.Drafts, d => d.Axis == KnowledgeAxis.Item);
        Assert.Equal("note.item.M-001", draft.Id);
        Assert.Contains("M-001", draft.Body);
        Assert.Contains("A사", draft.Body);
        Assert.Equal(3, draft.Provenance.DistinctUsers);
        Assert.Contains("우연히 몰린 것인가", draft.Body);   // 승인자가 확인할 것
    }

    [Fact]
    public void SingleUserCooccurrence_StaysARecord_NotKnowledge()
    {
        // 한 사람의 반복은 레코드다 — org 문서로 오르면 그 사람이 무엇을 사는지 드러난다.
        var entities = new EntityStore();
        for (var i = 0; i < 10; i++) Observe(entities, "alice", "M-001", "A사");

        var result = Aggregator(entities, new KnowledgeStore()).Aggregate(T0);

        Assert.DoesNotContain(result.Drafts, d => d.Axis == KnowledgeAxis.Item);
        Assert.Contains(result.Skipped, s => s.Contains("k인 게이트"));
    }
}

public class EntityApiTests
{
    private static ObservationEvent Event(string user, string material, string vendor) => new(
        EventId: Guid.NewGuid().ToString(), SessionId: "s", UserId: user,
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo("https://proc/pr/create", "구매요청 등록", null),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "품목코드", material, "txtMat", new()),
            new("n3", "Edit", "거래처", vendor, "txtVendor", new()),
            new("n4", "Edit", "단가", "12000", "txtPrice", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new()));

    [Fact]
    public async Task Observations_PopulateEntities_ExcludingSensitiveValues()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        await client.PostAsJsonAsync("/v1/observations", Event("alice", "M-001", "A사"));
        await client.PostAsJsonAsync("/v1/observations", Event("bob", " m-001 ", "A사"));

        var entities = await client.GetFromJsonAsync<JsonElement>("/v1/entities");

        var items = entities.GetProperty("entities").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "Item").ToList();
        var item = Assert.Single(items);
        Assert.Equal("M-001", item.GetProperty("key").GetString());
        Assert.Equal(2, item.GetProperty("distinctUsers").GetInt32());

        // 단가는 민감이라 엔티티가 되지 않는다 — 값 자체가 서버에 도달하지 않는다
        Assert.DoesNotContain(entities.GetProperty("entities").EnumerateArray(),
            e => e.GetProperty("key").GetString() == "12000");

        var link = Assert.Single(entities.GetProperty("links").EnumerateArray());
        Assert.Equal(2, link.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Splits_AreReported_NotMerged()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        await client.PostAsJsonAsync("/v1/observations", Event("alice", "M-001", "A사"));
        await client.PostAsJsonAsync("/v1/observations", Event("bob", "M001", "A사"));

        var entities = await client.GetFromJsonAsync<JsonElement>("/v1/entities");

        Assert.Equal(1, entities.GetProperty("splitCandidates").GetInt32());
        var split = Assert.Single(entities.GetProperty("splits").EnumerateArray());
        Assert.Equal(new[] { "M-001", "M001" },
            split.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ToArray());
    }
}
