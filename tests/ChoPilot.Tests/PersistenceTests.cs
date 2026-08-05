using System.Text;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;
using Xunit;

namespace ChoPilot.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 저장소 영속화 (ARCHITECTURE §11).
//
// 시험의 형태가 전부 같다: 저장소를 만들고, 쓰고, <b>버리고, 같은 저널로 다시 만든다</b>.
// 재시작을 흉내 내는 유일한 방법이고, 인메모리 상태를 들여다보는 시험은 이걸 증명하지 못한다.
//
// 특히 겨누는 것:
//   - 지식은 연산(제출·승인·폐기)을 남긴다. 문서만 남기면 승인이 대기본을 지운 사실이 사라진다.
//   - 지식 버전은 되감기면 안 된다. 되감기면 재추론 백오프가 온톨로지 변경으로 오인한다.
//   - 손상된 마지막 줄(쓰기 도중 종료)은 앞부분을 죽이지 않는다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>테스트용 저널 디렉터리. 재시작은 같은 경로로 팩토리를 새로 만드는 것이다.</summary>
internal sealed class TempJournals : IDisposable
{
    public TempJournals() =>
        Directory = Path.Combine(Path.GetTempPath(), "chopilot-journal-" + Guid.NewGuid().ToString("N"));

    public string Directory { get; }

    /// <summary>"재시작" — 같은 디스크를 읽는 새 팩토리.</summary>
    public FileJournalFactory Restart() => new(Directory);

    public string PathOf(string name) => Path.Combine(Directory, name + ".jsonl");

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }
}

public class JsonLinesJournalTests
{
    private sealed record Row(string Name, int Value, string? Optional = null);

    [Fact]
    public void AppendAndLoad_RoundTripsInOrder()
    {
        using var temp = new TempJournals();
        var journal = temp.Restart().Open<Row>("rows");
        journal.Append(new Row("a", 1));
        journal.Append(new Row("b", 2));

        var loaded = temp.Restart().Open<Row>("rows").Load();

        Assert.Equal(new[] { "a", "b" }, loaded.Select(r => r.Name));
        Assert.Equal(2, loaded[1].Value);
    }

    [Fact]
    public void NewlinesInValues_DoNotBreakLineFraming()
    {
        // 한 줄에 레코드 하나라는 전제가 깨지면 복원이 통째로 어긋난다.
        using var temp = new TempJournals();
        temp.Restart().Open<Row>("rows").Append(new Row("여러\n줄\r\n짜리", 1));

        var loaded = Assert.Single(temp.Restart().Open<Row>("rows").Load());
        Assert.Equal("여러\n줄\r\n짜리", loaded.Name);
    }

    [Fact]
    public void TruncatedLastLine_IsSkipped_NotFatal()
    {
        // 쓰기 도중 프로세스가 죽으면 마지막 줄이 잘린다. 전체를 버리면 멀쩡한 앞부분까지 잃는다.
        using var temp = new TempJournals();
        var journal = temp.Restart().Open<Row>("rows");
        journal.Append(new Row("a", 1));
        journal.Append(new Row("b", 2));
        File.AppendAllText(temp.PathOf("rows"), "{\"Name\":\"c\",\"Val", new UTF8Encoding(false));

        var restarted = temp.Restart().Open<Row>("rows");
        var loaded = restarted.Load();

        Assert.Equal(new[] { "a", "b" }, loaded.Select(r => r.Name));
        Assert.Equal(1, restarted.Corrupt);      // 조용히 넘어가면 유실을 아무도 모른다
        Assert.Equal(2, restarted.Restored);
    }

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        using var temp = new TempJournals();
        Assert.Empty(temp.Restart().Open<Row>("never-written").Load());
    }

    [Fact]
    public void NullFactory_PersistsNothing()
    {
        var journal = NullJournalFactory.Instance.Open<Row>("rows");
        journal.Append(new Row("a", 1));
        Assert.Empty(journal.Load());
    }
}

public class StoreRestartTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MappingCache_SurvivesRestart()
    {
        // 이 캐시를 잃는다는 것은 이미 치른 AI 추론 비용을 다시 치른다는 뜻이다.
        using var temp = new TempJournals();
        var entry = new MappingEntry("sig1", "global", null, "PurchaseRequest", null,
            new List<FieldMapping> { new("n2", "Material", 0.9, "ai", false) }, 0.9, "trusted");

        new InMemoryMappingCache(temp.Restart()).Put(entry);

        var restored = new InMemoryMappingCache(temp.Restart()).Get("sig1", "global");
        Assert.NotNull(restored);
        Assert.Equal("Material", Assert.Single(restored.Mapping).Concept);
    }

    [Fact]
    public void MappingCache_LastWriteWins()
    {
        using var temp = new TempJournals();
        var cache = new InMemoryMappingCache(temp.Restart());
        var entry = new MappingEntry("sig1", "global", null, "PurchaseRequest", null,
            new List<FieldMapping>(), 0.5, "pending_review");
        cache.Put(entry);
        cache.Put(entry with { Confidence = 1.0, Status = "trusted" });

        var restored = new InMemoryMappingCache(temp.Restart()).Get("sig1", "global")!;
        Assert.Equal(1.0, restored.Confidence);
        Assert.Single(new InMemoryMappingCache(temp.Restart()).All());
    }

    [Fact]
    public void Audit_SurvivesRestart_AndKeepsSequenceMonotonic()
    {
        // 재시작 후 Seq가 1부터 다시 시작하면 감사 로그의 순서가 뒤엉킨다.
        using var temp = new TempJournals();
        var entry = new MappingEntry("sig1", "global", null, "PurchaseRequest", null,
            new List<FieldMapping>(), 0.9, "trusted");
        var evt = TestEvents.Evt("e1", T0);
        var result = new MappingResolver.ResolveResult(entry, MappingResolver.Source.TrustedCache);

        var audit = new AuditService(temp.Restart());
        audit.Record(evt, "sig1", result, 12);
        audit.Record(evt, "sig1", result, 13);

        var restarted = new AuditService(temp.Restart());
        Assert.Equal(2, restarted.Count);

        var next = restarted.Record(evt, "sig1", result, 14);
        Assert.Equal(3, next.Seq);
    }

    [Fact]
    public void Decisions_SurviveRestart()
    {
        using var temp = new TempJournals();
        new DecisionLog(temp.Restart()).Record("promote", "kim", "sig1", "global", 1.0, "detail");

        var restarted = new DecisionLog(temp.Restart());
        Assert.Equal(1, restarted.Count);
        Assert.Equal("kim", Assert.Single(restarted.Snapshot()).Actor);
        Assert.Equal(2, restarted.Record("correction", "lee", "sig2", "personal:lee", 1.0, "d").Seq);
    }

    [Fact]
    public void Completions_SurviveRestart()
    {
        using var temp = new TempJournals();
        new CompletionStore(temp.Restart()).Record(new CompletionRecord(
            "PurchaseRequest", "alice", "sig", new[] { "Material", "Vendor" }, new[] { "Material" }, T0));

        var stat = Assert.Single(new CompletionStore(temp.Restart()).Stats());
        Assert.Equal(1, stat.Completions);
        Assert.Equal(1.0, Assert.Single(stat.Concepts, c => c.Concept == "Material").FillRate);
        Assert.Equal(0.0, Assert.Single(stat.Concepts, c => c.Concept == "Vendor").FillRate);
    }

    [Fact]
    public void UnknownConcepts_SurviveRestart()
    {
        using var temp = new TempJournals();
        new UnknownConceptLog(temp.Restart())
            .Record("alice", "sig", "PurchaseRequest", new[] { "결제조건" }, T0);

        var candidate = Assert.Single(new UnknownConceptLog(temp.Restart()).Candidates());
        Assert.Equal("결제조건", candidate.Term);
    }

    [Fact]
    public void Observations_SurviveRestart()
    {
        using var temp = new TempJournals();
        var entry = new MappingEntry("sig1", "global", null, "PurchaseRequest", null,
            new List<FieldMapping>(), 0.9, "trusted");
        var bo = new BusinessObject("PurchaseRequest", new Dictionary<string, string?>(), 0.9, "ai");

        new ObservationStore(temp.Restart()).Put("e1", TestEvents.Evt("e1", T0), entry, bo);

        var restarted = new ObservationStore(temp.Restart());
        Assert.NotNull(restarted.Get("e1"));
        Assert.Equal("e1", Assert.Single(restarted.List()).ObservationId);

        // 새 관측의 Seq가 복원분과 겹치면 측정 UI의 적재 순서가 무너진다.
        restarted.Put("e2", TestEvents.Evt("e2", T0.AddSeconds(1)), entry, bo);
        Assert.Equal(new[] { "e1", "e2" }, restarted.List().Select(o => o.ObservationId));
    }

    [Fact]
    public void Uep_ReplaysVisitsInOrder_SoTransitionsSurvive()
    {
        // 전이는 "직전 화면"에 의존한다 — 저널 순서가 곧 정확성이다.
        using var temp = new TempJournals();
        var uep = new UepStore(temp.Restart());
        uep.RecordVisit("alice", "pr", T0, "/pr/create", "구매요청");
        uep.RecordVisit("alice", "po", T0.AddMinutes(1), "/po/create", "발주");
        uep.RecordVisit("alice", "pr", T0.AddMinutes(2), "/pr/create", "구매요청");
        uep.RecordVisit("alice", "po", T0.AddMinutes(3), "/po/create", "발주");

        var restarted = new UepStore(temp.Restart());
        var transition = Assert.Single(restarted.NextScreens("alice", "pr"));

        Assert.Equal("po", transition.ToSignature);
        Assert.Equal(2, transition.Count);
        Assert.Equal(2, restarted.Get("alice")!.Screens.Count);
    }

    [Fact]
    public void Entities_ReplayObservations_SoLinksAndUsersSurvive()
    {
        using var temp = new TempJournals();
        var mentions = new[]
        {
            new EntityMention(new EntityKey("Item", "M-001"), "M-001", "Material"),
            new EntityMention(new EntityKey("Company", "A사"), "A사", "Vendor"),
        };

        var entities = new EntityStore(temp.Restart());
        entities.Record(mentions, "alice", "sig", T0);
        entities.Record(mentions, "bob", "sig", T0);

        var restarted = new EntityStore(temp.Restart());
        var link = Assert.Single(restarted.Links());
        Assert.Equal(2, link.Count);
        Assert.Equal(2, link.DistinctUsers);      // 접힌 결과가 아니라 입력을 남겼기에 복원된다
    }

    [Fact]
    public void Suggestions_SurviveRestart_WithDecisionsIntact()
    {
        using var temp = new TempJournals();
        var hint = new GuideHint("sg:1", "guide", "Vendor", "거래처 입력이 남았습니다", false);

        var store = new SuggestionFeedbackStore(temp.Restart());
        store.RecordImpressions("alice", "obs1", "sig", "PurchaseRequest", new[] { hint }, T0);
        store.Decide("alice", "obs1", "sg:1", SuggestionOutcome.Rejected, T0.AddMinutes(1));

        var stats = new SuggestionFeedbackStore(temp.Restart()).Stats();
        Assert.Equal(1, stats.Impressions);       // 복원이 분모를 부풀리지 않는다
        Assert.Equal(1, stats.Rejected);
        Assert.Equal(0, stats.Pending);           // 판단이 노출을 덮는다
    }
}

public class KnowledgeRestartTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static KnowledgeDoc Draft(string id, string term) => new(
        Id: id, Axis: KnowledgeAxis.Domain, Kind: KnowledgeKind.Curated, Type: KnowledgeType.Concept,
        Scope: "global", Title: $"개념: {term}",
        Concept: new Concept(term, "string", new[] { term }, Sensitive: true),
        Required: null, Hint: null, Body: "본문", Version: 0,
        Status: KnowledgeStatus.PendingReview, Provenance: KnowledgeProvenance.Seed,
        CreatedBy: "aggregator", ApprovedBy: null, UpdatedAt: T0);

    [Fact]
    public void PublishedConcept_SurvivesRestart_AndStaysCompiled()
    {
        using var temp = new TempJournals();
        var store = new KnowledgeStore(temp.Restart());
        store.Submit(Draft("concept.결제조건", "결제조건"), "aggregator", T0);
        store.Approve("concept.결제조건", "kim", T0);

        var restarted = new KnowledgeStore(temp.Restart());
        Assert.NotNull(restarted.Current.Resolve("결제조건"));
        Assert.Equal(KnowledgeStatus.Published, restarted.Get("concept.결제조건")!.Status);
    }

    [Fact]
    public void ApprovalRemovesThePendingDraft_AcrossRestart()
    {
        // 문서만 남기면 "승인이 대기본을 지웠다"는 사실이 사라져 재시작 후 초안이 되살아난다.
        using var temp = new TempJournals();
        var store = new KnowledgeStore(temp.Restart());
        store.Submit(Draft("concept.결제조건", "결제조건"), "aggregator", T0);
        store.Approve("concept.결제조건", "kim", T0);

        var restarted = new KnowledgeStore(temp.Restart());
        Assert.Null(restarted.PendingDraft("concept.결제조건"));
        Assert.Empty(restarted.List(status: KnowledgeStatus.PendingReview));
    }

    [Fact]
    public void PendingDraft_SurvivesRestart_Unapproved()
    {
        using var temp = new TempJournals();
        new KnowledgeStore(temp.Restart()).Submit(Draft("concept.납품장소", "납품장소"), "aggregator", T0);

        var restarted = new KnowledgeStore(temp.Restart());
        Assert.NotNull(restarted.PendingDraft("concept.납품장소"));
        Assert.Null(restarted.Current.Resolve("납품장소"));   // 승인 전에는 온톨로지가 아니다
    }

    [Fact]
    public void Version_DoesNotRewind_AcrossRestart()
    {
        // 버전이 되감기면 저신뢰 매핑의 재추론 백오프가 "온톨로지가 바뀌었다"고 오인한다.
        using var temp = new TempJournals();
        var store = new KnowledgeStore(temp.Restart());
        store.Submit(Draft("concept.a", "a"), "aggregator", T0);
        store.Approve("concept.a", "kim", T0);
        store.Submit(Draft("concept.b", "b"), "aggregator", T0);
        store.Approve("concept.b", "kim", T0);
        var live = store.Current.Version;

        var restarted = new KnowledgeStore(temp.Restart());
        Assert.Equal(live, restarted.Current.Version);
        Assert.True(restarted.Current.Version > 1);
    }

    [Fact]
    public void DeprecationSurvivesRestart_AndStaysOutOfTheOntology()
    {
        using var temp = new TempJournals();
        var store = new KnowledgeStore(temp.Restart());
        store.Deprecate("concept.Vendor", "kim", T0);   // 시드 문서를 폐기

        var restarted = new KnowledgeStore(temp.Restart());
        Assert.Equal(KnowledgeStatus.Deprecated, restarted.Get("concept.Vendor")!.Status);
        Assert.Null(restarted.Current.Resolve("Vendor"));
    }

    [Fact]
    public void RevisedSeedRule_OverridesTheSeed_AcrossRestart()
    {
        using var temp = new TempJournals();
        var revision = new KnowledgeDoc(
            Id: "rule.required.PurchaseRequest", Axis: KnowledgeAxis.Domain, Kind: KnowledgeKind.Curated,
            Type: KnowledgeType.RequiredFields, Scope: "global", Title: "필수 필드 개정",
            Concept: null, Required: new RequiredFieldsRule("PurchaseRequest", new[] { "Material", "Vendor" }),
            Hint: null, Body: "본문", Version: 0, Status: KnowledgeStatus.PendingReview,
            Provenance: KnowledgeProvenance.Seed, CreatedBy: "aggregator", ApprovedBy: null, UpdatedAt: T0);

        var store = new KnowledgeStore(temp.Restart());
        store.Submit(revision, "aggregator", T0);
        store.Approve(revision.Id, "kim", T0);

        // 부팅은 시드를 먼저 깔고 저널을 얹는다 — 개정본이 시드를 덮어야 한다.
        var restarted = new KnowledgeStore(temp.Restart());
        Assert.Equal(new[] { "Material", "Vendor" }, restarted.Current.RequiredFor("PurchaseRequest"));
    }
}
