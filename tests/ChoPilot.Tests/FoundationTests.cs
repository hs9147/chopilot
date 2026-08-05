using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ChoPilot.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 기반 정보 축.
//
// 이 축의 시험은 "지식이 잘 만들어지는가"가 아니라 <b>"없는 것을 있다고 하지 않는가"</b>다.
// 마스터 없이 대사하면 관측 전량이 미등록이 되고, 키 공간이 어긋난 것을 미등록으로 세면
// 경보가 전부 거짓이 된다. 아래 시험 대부분이 그 두 오류를 겨눈다.
//
// 네트워크는 타지 않는다 — 모든 HTTP 출처는 스텁 핸들러로 시험한다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>요청 본문을 기록하고 지정한 응답을 돌려주는 스텁. 실제 소켓을 열지 않는다.</summary>
internal sealed class HttpStub : HttpMessageHandler
{
    private readonly Func<string, HttpResponseMessage> _respond;

    public HttpStub(Func<string, HttpResponseMessage> respond) => _respond = respond;

    public List<string> Urls { get; } = new();
    public List<string> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Urls.Add(request.RequestUri!.ToString());
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Bodies.Add(body);
        return _respond(body);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string body, string mediaType, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
}

/// <summary>조회 결과를 통째로 지정하는 가짜 출처 — 저장소·집계기 시험용.</summary>
internal sealed class FakeFoundationSource : IFoundationSource
{
    private readonly Func<FoundationQuery, FoundationFetch> _fetch;

    public FakeFoundationSource(string id, string kind, Func<FoundationQuery, FoundationFetch> fetch,
        bool requiresNetwork = true)
    {
        Id = id;
        Kind = kind;
        _fetch = fetch;
        RequiresNetwork = requiresNetwork;
    }

    public string Id { get; }
    public string Title => $"가짜 출처 {Id}";
    public string Kind { get; }
    public string Origin => $"fake:{Id}";
    public string License => "테스트 전용";
    public bool RequiresNetwork { get; }

    public Task<FoundationFetch> FetchAsync(FoundationQuery query, CancellationToken ct = default) =>
        Task.FromResult(_fetch(query));
}

internal static class TestFoundation
{
    public static readonly DateTimeOffset At = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    public static FoundationFact Fact(string sourceId, string kind, string key, string? label = null) =>
        FoundationFact.Create(sourceId, kind, key, label)!;

    public static FoundationStore Store(params IFoundationSource[] sources) => new(sources);

    /// <summary>기반 출처·완료 신호 없이 만든 집계기 — 해당 축은 조용하고 나머지만 돈다.</summary>
    public static AxisAggregator Aggregator(
        UnknownConceptLog unknown, UepStore uep, SuggestionFeedbackStore suggestions,
        KnowledgeStore knowledge, EntityStore entities,
        FoundationStore? foundation = null, CompletionStore? completions = null,
        int minSupport = AxisAggregator.DefaultMinSupport,
        int minUsers = AxisAggregator.DefaultMinDistinctUsers)
    {
        foundation ??= new FoundationStore(Array.Empty<IFoundationSource>());
        return new AxisAggregator(unknown, uep, suggestions, knowledge, entities,
            foundation, new FoundationReconciler(entities, foundation),
            completions ?? new CompletionStore(), minSupport, minUsers);
    }
}

public class FoundationFactTests
{
    [Fact]
    public void Create_NormalizesKey_TheSameWayObservationDoes()
    {
        // 관측과 다른 정규화를 쓰면 " a사 "가 마스터의 "A사"와 어긋나 전부 미등록이 된다.
        var fact = TestFoundation.Fact("s", FoundationKind.Company, " a사 ");
        Assert.Equal("A사", fact.Key);
        Assert.Equal(EntityResolver.Normalize(" a사 "), fact.Key);
    }

    [Fact]
    public void Create_ReturnsNull_ForEmptyKey() =>
        Assert.Null(FoundationFact.Create("s", FoundationKind.Company, "   ", "빈 키"));

    [Fact]
    public void Create_TruncatesKey_AndCapsAttributes()
    {
        var attrs = Enumerable.Range(0, 50).ToDictionary(i => $"k{i}", i => $"v{i}");
        var fact = FoundationFact.Create("s", FoundationKind.Item, new string('A', 500), "긴 키", attrs)!;

        Assert.Equal(FoundationFact.MaxKeyLength, fact.Key.Length);
        Assert.Equal(FoundationFact.MaxAttributes, fact.Attributes.Count);
    }

    [Fact]
    public void Create_FallsBackToRawKey_WhenLabelMissing() =>
        Assert.Equal("M-001", TestFoundation.Fact("s", FoundationKind.Item, " M-001 ").Label);
}

public class FoundationMasterTests
{
    [Fact]
    public void Empty_CoversNothing()
    {
        Assert.False(FoundationMaster.Empty.Covers(FoundationKind.Company));
        Assert.Equal(0, FoundationMaster.Empty.Count);
    }

    [Fact]
    public void Lookup_FoldsTheSameNormalization()
    {
        var master = new FoundationMaster(new[] { TestFoundation.Fact("s", FoundationKind.Company, "A사") });
        Assert.NotNull(master.Lookup(FoundationKind.Company, " a사 "));
        Assert.Null(master.Lookup(FoundationKind.Company, "B사"));
        Assert.Null(master.Lookup(FoundationKind.Item, "A사"));   // 종류가 다르면 다른 키다
    }

    [Fact]
    public void LaterFactWins_ForTheSameKindAndKey()
    {
        var master = new FoundationMaster(new[]
        {
            TestFoundation.Fact("old", FoundationKind.Company, "A사", "예전 이름"),
            TestFoundation.Fact("new", FoundationKind.Company, "A사", "현재 이름"),
        });

        Assert.Equal("현재 이름", master.Lookup(FoundationKind.Company, "A사")!.Label);
        Assert.Equal(1, master.Count);
    }
}

public class EmbeddedFoundationSourceTests
{
    [Fact]
    public async Task CompletesSynchronously_AndNeedsNoNetwork()
    {
        var source = new EmbeddedFoundationSource();
        Assert.False(source.RequiresNetwork);

        // 부팅 시 블로킹 없이 읽힌다는 계약 — 여기서 I/O가 생기면 시작이 외부 API에 묶인다.
        var task = source.FetchAsync(FoundationQuery.Empty);
        Assert.True(task.IsCompletedSuccessfully);

        var master = new FoundationMaster((await task).Facts);
        Assert.True(master.Covers(FoundationKind.Currency));
        Assert.True(master.Covers(FoundationKind.UnitOfMeasure));
        Assert.Equal("대한민국 원", master.Lookup(FoundationKind.Currency, "krw")!.Label);
    }

    [Fact]
    public async Task CarriesNoCompanyOrItemMaster()
    {
        // 그럴듯한 거래처 목록을 심어 두면 대사가 거짓으로 통과한다.
        // 빈 마스터는 Covers=false로 "대사 불가"라고 정직하게 보고된다.
        var master = new FoundationMaster(
            (await new EmbeddedFoundationSource().FetchAsync(FoundationQuery.Empty)).Facts);

        Assert.False(master.Covers(FoundationKind.Company));
        Assert.False(master.Covers(FoundationKind.Item));
    }
}

public class ExchangeRateSourceTests
{
    private const string Ok = """
        {"result":"success","time_last_update_unix":1767225600,"base_code":"KRW",
         "rates":{"KRW":1,"USD":0.00072,"JPY":0.11}}
        """;

    [Fact]
    public async Task ParsesRates_IntoExchangeRateFacts()
    {
        var handler = new HttpStub(_ => HttpStub.Json(Ok));
        var source = new ExchangeRateSource(new HttpClient(handler));

        var fetch = await source.FetchAsync(FoundationQuery.Empty);

        Assert.True(fetch.Ok);
        Assert.Equal(3, fetch.Facts.Count);
        var usd = Assert.Single(fetch.Facts, f => f.Key == "USD");
        Assert.Equal(FoundationKind.ExchangeRate, usd.Kind);
        Assert.Equal("0.00072", usd.Attributes["rate"]);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), usd.AsOf);
    }

    [Fact]
    public async Task FailedResult_BecomesError_NotAnEmptyList()
    {
        // 조용한 0건은 "마스터에 없다"와 구분되지 않는다 — 대사가 통째로 뒤집힌다.
        var handler = new HttpStub(_ => HttpStub.Json("""{"result":"error","error-type":"invalid-key"}"""));
        var fetch = await new ExchangeRateSource(new HttpClient(handler)).FetchAsync(FoundationQuery.Empty);

        Assert.False(fetch.Ok);
        Assert.Contains("error", fetch.Error);
        Assert.Empty(fetch.Facts);
    }
}

public class PublicHolidaySourceTests
{
    private const string TwoItems = """
        {"response":{"header":{"resultCode":"00","resultMsg":"NORMAL SERVICE."},
         "body":{"items":{"item":[
            {"dateKind":"01","dateName":"1월1일","isHoliday":"Y","locdate":20260101,"seq":1},
            {"dateKind":"01","dateName":"설날","isHoliday":"Y","locdate":20260217,"seq":2}]}}}}
        """;

    private const string SingleItem = """
        {"response":{"header":{"resultCode":"00"},
         "body":{"items":{"item":{"dateName":"현충일","isHoliday":"Y","locdate":20260606}}}}}
        """;

    [Fact]
    public async Task QueriesThisYearAndNext_AndFormatsDatesAsKeys()
    {
        var handler = new HttpStub(_ => HttpStub.Json(TwoItems));
        var fetch = await new PublicHolidaySource(new HttpClient(handler), "KEY").FetchAsync(FoundationQuery.Empty);

        Assert.Equal(2, handler.Urls.Count);            // 올해 + 내년
        Assert.Equal(2, fetch.Facts.Select(f => f.Key).Distinct().Count());
        Assert.Contains(fetch.Facts, f => f.Key == "2026-01-01" && f.Label == "1월1일");
        Assert.All(fetch.Facts, f => Assert.Equal("Y", f.Attributes["isHoliday"]));
    }

    [Fact]
    public async Task HandlesSingleItem_ComingBackAsAnObject()
    {
        // 1건이면 배열이 아니라 객체로 온다 — 배열만 받으면 공휴일이 조용히 사라진다.
        var handler = new HttpStub(_ => HttpStub.Json(SingleItem));
        var fetch = await new PublicHolidaySource(new HttpClient(handler), "KEY", yearsAhead: 0)
            .FetchAsync(FoundationQuery.Empty);

        var fact = Assert.Single(fetch.Facts);
        Assert.Equal("2026-06-06", fact.Key);
    }

    [Fact]
    public async Task XmlErrorDocument_BecomesError()
    {
        // 키 오류·쿼터 초과 시 _type=json이어도 XML 오류 문서가 온다.
        var handler = new HttpStub(_ => HttpStub.Text(
            "<OpenAPI_ServiceResponse><cmmMsgHeader><returnAuthMsg>SERVICE_KEY_IS_NOT_REGISTERED_ERROR",
            "application/xml"));

        var fetch = await new PublicHolidaySource(new HttpClient(handler), "BAD").FetchAsync(FoundationQuery.Empty);

        Assert.False(fetch.Ok);
        Assert.Contains("JSON이 아닌 응답", fetch.Error);
    }

    [Fact]
    public void Origin_DoesNotLeakTheServiceKey()
    {
        // Origin은 지식 문서 본문에 출처로 그대로 실린다.
        var source = new PublicHolidaySource(new HttpClient(new HttpStub(_ => HttpStub.Json("{}"))), "SECRET");
        Assert.DoesNotContain("SECRET", source.Origin);
    }
}

public class BusinessStatusSourceTests
{
    private const string Ok = """
        {"status_code":"OK","data":[
          {"b_no":"1234567890","b_stt":"계속사업자","tax_type":"부가가치세 일반과세자"},
          {"b_no":"9999999999","b_stt":"","tax_type":"국세청에 등록되지 않은 사업자등록번호입니다."}]}
        """;

    [Theory]
    [InlineData("1234567890", "1234567890")]
    [InlineData("123-45-67890", "1234567890")]
    [InlineData("A사", null)]
    [InlineData("12345", null)]
    public void AsBusinessNumber_AcceptsOnlyTenDigits(string key, string? expected) =>
        Assert.Equal(expected, BusinessStatusSource.AsBusinessNumber(key));

    [Fact]
    public async Task AsksOnlyAboutBusinessNumbers_FromObservedKeys()
    {
        var handler = new HttpStub(_ => HttpStub.Json(Ok));
        var query = new FoundationQuery(new Dictionary<string, IReadOnlyList<string>>
        {
            [FoundationKind.Company] = new[] { "123-45-67890", "A사", "9999999999" },
        });

        var fetch = await new BusinessStatusSource(new HttpClient(handler), "KEY").FetchAsync(query);

        // 상호명은 애초에 물어볼 수 없다 — 요청 본문에 들어가지 않는다
        Assert.DoesNotContain("A사", handler.Bodies[0]);
        Assert.Contains("1234567890", handler.Bodies[0]);

        // b_stt가 빈 행은 미등록 번호다 — 마스터에 넣으면 대사를 거짓 통과시킨다
        var fact = Assert.Single(fetch.Facts);
        Assert.Equal("1234567890", fact.Key);
        Assert.Equal("계속사업자", fact.Label);
    }

    [Fact]
    public async Task MakesNoRequest_WhenNothingObservedIsAskable()
    {
        var handler = new HttpStub(_ => HttpStub.Json(Ok));
        var query = new FoundationQuery(new Dictionary<string, IReadOnlyList<string>>
        {
            [FoundationKind.Company] = new[] { "A사", "B사" },
        });

        var fetch = await new BusinessStatusSource(new HttpClient(handler), "KEY").FetchAsync(query);

        Assert.Empty(handler.Urls);
        Assert.True(fetch.Ok);
        Assert.Empty(fetch.Facts);
    }
}

public class McpFoundationSourceTests
{
    private static McpSourceOptions Options(string? keyArgument = null) => new()
    {
        Endpoint = "https://mcp.example/rpc",
        Tool = "list_vendors",
        Kind = FoundationKind.Company,
        KeyArgument = keyArgument,
    };

    private static string ToolResult(string text) =>
        """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"""
        + JsonSerializer.Serialize(text) + "}]}}";

    private static HttpStub Handler(string toolResponse) => new(body =>
        body.Contains("\"initialize\"") ? HttpStub.Json("""{"jsonrpc":"2.0","id":1,"result":{}}""")
        : body.Contains("notifications/initialized") ? new HttpResponseMessage(HttpStatusCode.Accepted)
        : HttpStub.Json(toolResponse));

    [Fact]
    public async Task Handshake_ThenToolCall_ParsesFacts()
    {
        var handler = Handler(ToolResult("""[{"code":"A사","name":"에이사"},{"code":"B사"}]"""));
        var fetch = await new McpFoundationSource(new HttpClient(handler), Options())
            .FetchAsync(FoundationQuery.Empty);

        Assert.True(fetch.Ok, fetch.Error);
        Assert.Equal(3, handler.Urls.Count);                     // initialize + initialized + tools/call
        Assert.Contains("\"list_vendors\"", handler.Bodies[2]);

        Assert.Equal(2, fetch.Facts.Count);
        var a = Assert.Single(fetch.Facts, f => f.Key == "A사");
        Assert.Equal("에이사", a.Label);
        Assert.Equal(FoundationKind.Company, a.Kind);
    }

    [Fact]
    public async Task ParsesServerSentEventResponses()
    {
        var sse = "event: message\ndata: " + ToolResult("""{"facts":[{"key":"C사"}]}""") + "\n\n";
        var handler = new HttpStub(body =>
            body.Contains("\"initialize\"") ? HttpStub.Json("""{"jsonrpc":"2.0","id":1,"result":{}}""")
            : body.Contains("notifications/initialized") ? new HttpResponseMessage(HttpStatusCode.Accepted)
            : HttpStub.Text(sse, "text/event-stream"));

        var fetch = await new McpFoundationSource(new HttpClient(handler), Options())
            .FetchAsync(FoundationQuery.Empty);

        Assert.True(fetch.Ok, fetch.Error);
        Assert.Equal("C사", Assert.Single(fetch.Facts).Key);
    }

    [Fact]
    public async Task KeyArgument_CarriesObservedKeysToTheTool()
    {
        var handler = Handler(ToolResult("[]"));
        var query = new FoundationQuery(new Dictionary<string, IReadOnlyList<string>>
        {
            [FoundationKind.Company] = new[] { "A사", "B사" },
        });

        await new McpFoundationSource(new HttpClient(handler), Options(keyArgument: "names")).FetchAsync(query);

        // 비ASCII는 \uXXXX로 이스케이프되어 나가므로 문자열 비교가 아니라 파싱으로 확인한다.
        using var sent = JsonDocument.Parse(handler.Bodies[2]);
        var names = sent.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("names");
        Assert.Equal(new[] { "A사", "B사" }, names.EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public async Task ToolError_BecomesFetchError()
    {
        var handler = Handler("""{"jsonrpc":"2.0","id":2,"result":{"isError":true,"content":[{"type":"text","text":"권한 없음"}]}}""");
        var fetch = await new McpFoundationSource(new HttpClient(handler), Options())
            .FetchAsync(FoundationQuery.Empty);

        Assert.False(fetch.Ok);
        Assert.Contains("오류를 반환했다", fetch.Error);
    }

    [Fact]
    public async Task UnparseableText_IsAnError_NotZeroFacts()
    {
        // 조용한 0건이면 "마스터가 비었다"로 보여 대사가 전량 미등록으로 뒤집힌다.
        var handler = Handler(ToolResult("거래처 목록을 찾을 수 없습니다."));
        var fetch = await new McpFoundationSource(new HttpClient(handler), Options())
            .FetchAsync(FoundationQuery.Empty);

        Assert.False(fetch.Ok);
        Assert.Empty(fetch.Facts);
    }

    [Fact]
    public async Task CapsFactCount()
    {
        var rows = string.Join(",", Enumerable.Range(0, McpFoundationSource.MaxFacts + 500)
            .Select(i => JsonSerializer.Serialize(new { key = $"V{i}" })));
        var handler = Handler(ToolResult($"[{rows}]"));

        var fetch = await new McpFoundationSource(new HttpClient(handler), Options())
            .FetchAsync(FoundationQuery.Empty);

        Assert.Equal(McpFoundationSource.MaxFacts, fetch.Facts.Count);
    }

    [Fact]
    public async Task StructuredContent_WinsOverText()
    {
        var handler = Handler("""
            {"jsonrpc":"2.0","id":2,"result":{
              "content":[{"type":"text","text":"[{\"key\":\"TEXT\"}]"}],
              "structuredContent":{"items":[{"key":"STRUCTURED"}]}}}
            """);

        var fetch = await new McpFoundationSource(new HttpClient(handler), Options())
            .FetchAsync(FoundationQuery.Empty);

        Assert.Equal("STRUCTURED", Assert.Single(fetch.Facts).Key);
    }
}

public class FoundationStoreTests
{
    [Fact]
    public void OfflineSources_AreAppliedAtConstruction()
    {
        var store = TestFoundation.Store(new EmbeddedFoundationSource());
        Assert.True(store.Master.Covers(FoundationKind.Currency));
    }

    [Fact]
    public async Task RegistrationOrder_DecidesTheWinner()
    {
        // 조회 완료 순서로 정하면 같은 입력에 마스터가 실행마다 달라진다.
        var first = new FakeFoundationSource("first", FoundationKind.Company,
            _ => new FoundationFetch("first",
                new[] { TestFoundation.Fact("first", FoundationKind.Company, "A사", "첫 출처") },
                null, TestFoundation.At));
        var second = new FakeFoundationSource("second", FoundationKind.Company,
            _ => new FoundationFetch("second",
                new[] { TestFoundation.Fact("second", FoundationKind.Company, "A사", "둘째 출처") },
                null, TestFoundation.At));

        var store = TestFoundation.Store(first, second);
        await store.RefreshAsync(FoundationQuery.Empty);

        Assert.Equal("둘째 출처", store.Master.Lookup(FoundationKind.Company, "A사")!.Label);
    }

    [Fact]
    public async Task FailedRefresh_KeepsPriorFacts_AndSurfacesTheError()
    {
        // 일시적 장애로 마스터가 비면 관측 전량이 미등록으로 보고되어 경보가 뒤집힌다.
        var fail = false;
        var source = new FakeFoundationSource("flaky", FoundationKind.Company,
            _ => fail
                ? FoundationFetch.Failed("flaky", "503", TestFoundation.At)
                : new FoundationFetch("flaky",
                    new[] { TestFoundation.Fact("flaky", FoundationKind.Company, "A사") },
                    null, TestFoundation.At));

        var store = TestFoundation.Store(source);
        await store.RefreshAsync(FoundationQuery.Empty);
        fail = true;
        await store.RefreshAsync(FoundationQuery.Empty);

        Assert.NotNull(store.Master.Lookup(FoundationKind.Company, "A사"));
        var status = Assert.Single(store.Status());
        Assert.Equal("503", status.Error);
        Assert.Equal(1, status.Facts);
    }

    [Fact]
    public async Task ThrowingSource_DoesNotStopTheOthers()
    {
        var boom = new FakeFoundationSource("boom", FoundationKind.Item,
            _ => throw new InvalidOperationException("터짐"));
        var good = new FakeFoundationSource("good", FoundationKind.Item,
            _ => new FoundationFetch("good",
                new[] { TestFoundation.Fact("good", FoundationKind.Item, "M-001") }, null, TestFoundation.At));

        var store = TestFoundation.Store(boom, good);
        var results = await store.RefreshAsync(FoundationQuery.Empty);

        Assert.Contains(results, r => r.SourceId == "boom" && !r.Ok);
        Assert.True(store.Master.Covers(FoundationKind.Item));
    }

    [Fact]
    public void QueryFrom_GroupsObservedKeysByEntityType()
    {
        var entities = new EntityStore();
        entities.Record(new[]
        {
            new EntityMention(new EntityKey("Item", "M-001"), "M-001", "Material"),
            new EntityMention(new EntityKey("Company", "A사"), "A사", "Vendor"),
        }, "alice", "sig", TestFoundation.At);

        var query = FoundationStore.QueryFrom(entities);

        Assert.Equal(new[] { "M-001" }, query.For(FoundationKind.Item));
        Assert.Equal(new[] { "A사" }, query.For(FoundationKind.Company));
        Assert.Empty(query.For(FoundationKind.Holiday));
    }
}

public class FoundationReconcilerTests
{
    private static EntityStore Entities(params (string Type, string Key, string User)[] observed)
    {
        var store = new EntityStore();
        foreach (var (type, key, user) in observed)
            store.Record(new[] { new EntityMention(new EntityKey(type, key), key, "Vendor") },
                user, "sig", TestFoundation.At);
        return store;
    }

    private static FoundationStore Master(params FoundationFact[] facts) =>
        MasterAsync(facts).GetAwaiter().GetResult();

    private static async Task<FoundationStore> MasterAsync(FoundationFact[] facts)
    {
        var kind = facts.Length > 0 ? facts[0].Kind : FoundationKind.Company;
        var store = TestFoundation.Store(new FakeFoundationSource("master", kind,
            _ => new FoundationFetch("master", facts, null, TestFoundation.At)));
        await store.RefreshAsync(FoundationQuery.Empty);
        return store;
    }

    [Fact]
    public void NoMaster_IsNotUnmatched()
    {
        // 마스터가 없는데 미등록으로 세면 경보 100건이 전부 거짓이 된다.
        var report = new FoundationReconciler(
            Entities(("Company", "A사", "alice")),
            TestFoundation.Store()).Reconcile();

        Assert.Equal(0, report.Unmatched);
        Assert.Equal(1, report.NoMaster);
        Assert.Equal(ReconcileStatus.NoMaster, Assert.Single(report.Rows).Status);
        Assert.Contains(report.Notes, n => n.Contains("마스터가 없다"));
    }

    [Fact]
    public void MatchedAndUnmatched_AreSeparated()
    {
        var report = new FoundationReconciler(
            Entities(("Company", "A사", "alice"), ("Company", "Z사", "bob")),
            Master(TestFoundation.Fact("master", FoundationKind.Company, "A사", "에이사"))).Reconcile();

        Assert.Equal(1, report.Matched);
        Assert.Equal(1, report.Unmatched);
        Assert.Equal("에이사", Assert.Single(report.Rows, r => r.Key == "A사").Detail);
    }

    [Fact]
    public void KeySpaceMismatch_IsUnverifiable_NotUnmatched()
    {
        // 국세청은 사업자번호로만 답하고 화면은 상호명을 보여 준다 — 실제로 흔한 어긋남이다.
        var report = new FoundationReconciler(
            Entities(("Company", "A사", "alice")),
            Master(TestFoundation.Fact("master", FoundationKind.Company, "1234567890", "계속사업자"))).Reconcile();

        Assert.Equal(0, report.Unmatched);
        Assert.Equal(1, report.Unverifiable);
        Assert.Contains(report.Notes, n => n.Contains("사업자번호"));
    }

    [Fact]
    public void ObservedBusinessNumber_MatchesTheNumericMaster()
    {
        var report = new FoundationReconciler(
            Entities(("Company", "1234567890", "alice")),
            Master(TestFoundation.Fact("master", FoundationKind.Company, "1234567890", "계속사업자"))).Reconcile();

        Assert.Equal(1, report.Matched);
        Assert.Equal(0, report.Unverifiable);
    }
}

public class FoundationAxisAggregationTests
{
    private static (AxisAggregator Aggregator, KnowledgeStore Knowledge) Build(
        EntityStore entities, FoundationStore foundation)
    {
        var knowledge = new KnowledgeStore();
        return (TestFoundation.Aggregator(new UnknownConceptLog(), new UepStore(),
            new SuggestionFeedbackStore(), knowledge, entities, foundation), knowledge);
    }

    private static async Task<FoundationStore> Master(string kind, params FoundationFact[] facts)
    {
        var store = TestFoundation.Store(new FakeFoundationSource("free.api", kind,
            _ => new FoundationFetch("free.api", facts, null, TestFoundation.At)));
        await store.RefreshAsync(FoundationQuery.Empty);
        return store;
    }

    private static void Observe(EntityStore entities, string key, params string[] users)
    {
        foreach (var user in users)
            entities.Record(new[] { new EntityMention(new EntityKey("Company", key), key, "Vendor") },
                user, "sig", TestFoundation.At);
    }

    [Fact]
    public async Task NetworkSource_BecomesARegistrationDraft()
    {
        var foundation = await Master(FoundationKind.Company,
            TestFoundation.Fact("free.api", FoundationKind.Company, "A사"));
        var (aggregator, _) = Build(new EntityStore(), foundation);

        var draft = Assert.Single(aggregator.Aggregate(TestFoundation.At).Drafts,
            d => d.Id.StartsWith("note.foundation.source."));

        Assert.Equal(KnowledgeAxis.Foundation, draft.Axis);
        Assert.Equal(KnowledgeStatus.PendingReview, draft.Status);
        Assert.Contains("fake:free.api", draft.Body);
        Assert.Contains("권위로 인정한다", draft.Body);

        // 이 문서에는 관측이 없다 — k인 게이트를 걸면 출처 등록이 영원히 통과하지 못한다.
        Assert.Equal(0, draft.Provenance.DistinctUsers);
    }

    [Fact]
    public void EmbeddedSource_IsNotSubmittedForApproval()
    {
        // 승인이 묻는 것은 "이 외부 당사자를 믿는가"인데 내장 표준엔 물을 상대가 없다.
        var (aggregator, _) = Build(new EntityStore(),
            TestFoundation.Store(new EmbeddedFoundationSource()));

        Assert.DoesNotContain(aggregator.Aggregate(TestFoundation.At).Drafts,
            d => d.Id.StartsWith("note.foundation.source."));
    }

    [Fact]
    public async Task UnmatchedKeys_BecomeADraft_WhenGatesPass()
    {
        var entities = new EntityStore();
        Observe(entities, "A사", "alice", "bob");          // 마스터에 있다
        Observe(entities, "Z사", "alice", "bob", "carol"); // 없다 — 3회 3명

        var foundation = await Master(FoundationKind.Company,
            TestFoundation.Fact("free.api", FoundationKind.Company, "A사"));
        var (aggregator, _) = Build(entities, foundation);

        var draft = Assert.Single(aggregator.Aggregate(TestFoundation.At).Drafts,
            d => d.Id == "note.foundation.unmatched.Company");

        Assert.Equal(KnowledgeAxis.Foundation, draft.Axis);
        Assert.Contains("Z사", draft.Body);
        Assert.DoesNotContain("A사", draft.Body);          // 대사를 통과한 것은 결핍이 아니다
        Assert.Contains("이 목록은 마스터가 아니다", draft.Body);
    }

    [Fact]
    public async Task SingleUserGap_StaysARecord_NotKnowledge()
    {
        // 한 사람에게서만 나온 미등록 키는 그 사람이 무엇을 다루는지 드러낸다.
        var entities = new EntityStore();
        for (var i = 0; i < 10; i++) Observe(entities, "Z사", "alice");

        var foundation = await Master(FoundationKind.Company,
            TestFoundation.Fact("free.api", FoundationKind.Company, "A사"));
        var (aggregator, _) = Build(entities, foundation);
        var result = aggregator.Aggregate(TestFoundation.At);

        Assert.DoesNotContain(result.Drafts, d => d.Id == "note.foundation.unmatched.Company");
        Assert.Contains(result.Skipped, s => s.Contains("게이트를 넘은 미등록 키가 없다"));
    }

    [Fact]
    public void NoMaster_ProducesNoGapDraft()
    {
        var entities = new EntityStore();
        Observe(entities, "Z사", "alice", "bob", "carol");

        var (aggregator, _) = Build(entities, TestFoundation.Store());

        Assert.DoesNotContain(aggregator.Aggregate(TestFoundation.At).Drafts,
            d => d.Id.StartsWith("note.foundation.unmatched."));
    }

    [Fact]
    public async Task DeprecatedDraft_IsNotProposedAgain()
    {
        var entities = new EntityStore();
        Observe(entities, "Z사", "alice", "bob", "carol");

        var foundation = await Master(FoundationKind.Company,
            TestFoundation.Fact("free.api", FoundationKind.Company, "A사"));
        var (aggregator, knowledge) = Build(entities, foundation);

        var first = aggregator.Aggregate(TestFoundation.At).Drafts
            .Single(d => d.Id == "note.foundation.unmatched.Company");
        knowledge.Submit(first, "aggregator", TestFoundation.At);

        var second = aggregator.Aggregate(TestFoundation.At);
        Assert.DoesNotContain(second.Drafts, d => d.Id == "note.foundation.unmatched.Company");
        Assert.Contains(second.Skipped, s => s.Contains("이미 존재"));
    }
}

public class FoundationApiTests
{
    private static HttpRequestMessage WithUser(HttpMethod method, string url, string? user)
    {
        var req = new HttpRequestMessage(method, url);
        if (user is not null) req.Headers.Add(RequestUser.Header, user);
        return req;
    }

    private static ObservationEvent Event(string user, string vendor) => new(
        EventId: Guid.NewGuid().ToString(), SessionId: "s", UserId: user,
        CapturedAt: DateTimeOffset.UtcNow,
        Screen: new ScreenInfo("https://proc/pr/create", "구매요청 등록", null),
        Tree: new UiNode("n1", "Window", "구매요청 등록", null, null, new()
        {
            new("n2", "Edit", "거래처", vendor, "txtVendor", new()),
        }),
        Privacy: new PrivacyInfo("1.0", new()));

    [Fact]
    public async Task Foundation_ExposesTheEmbeddedMaster_WithNoNetworkSources()
    {
        using var server = new WebApplicationFactory<Program>();
        var foundation = await server.CreateClient().GetFromJsonAsync<JsonElement>("/v1/foundation");

        // 기본 설정에는 무료 API도 MCP도 켜져 있지 않다 — 테스트가 밖으로 나가지 않는다.
        var source = Assert.Single(foundation.GetProperty("sources").EnumerateArray());
        Assert.Equal("embedded.standards", source.GetProperty("id").GetString());
        Assert.False(source.GetProperty("requiresNetwork").GetBoolean());

        Assert.Contains(foundation.GetProperty("master").GetProperty("kinds").EnumerateArray(),
            k => k.GetProperty("kind").GetString() == FoundationKind.Currency);
    }

    [Fact]
    public async Task Reconcile_ReportsNoMaster_ForObservedVendors()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();
        await client.PostAsJsonAsync("/v1/observations", Event("alice", "A사"));

        var report = await client.GetFromJsonAsync<JsonElement>("/v1/foundation/reconcile");

        Assert.Equal(0, report.GetProperty("unmatched").GetInt32());
        Assert.Equal(1, report.GetProperty("noMaster").GetInt32());
        Assert.Contains(report.GetProperty("notes").EnumerateArray(),
            n => n.GetString()!.Contains("무료 API나 MCP 출처"));
    }

    [Fact]
    public async Task Refresh_RequiresUserHeader_AndIsRecordedAsADecision()
    {
        using var server = new WebApplicationFactory<Program>();
        var client = server.CreateClient();

        var anonymous = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/foundation/refresh", null));
        Assert.Equal(HttpStatusCode.BadRequest, anonymous.StatusCode);

        var refreshed = await client.SendAsync(WithUser(HttpMethod.Post, "/v1/foundation/refresh", "ops"));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var decisions = await client.GetFromJsonAsync<JsonElement>("/v1/decisions");
        Assert.Contains(decisions.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("action").GetString() == "foundation_refresh");
    }
}
