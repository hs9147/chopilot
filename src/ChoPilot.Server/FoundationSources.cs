using System.Net.Http.Json;
using System.Text.Json;
using ChoPilot.Core;

namespace ChoPilot.Server;

// ─────────────────────────────────────────────────────────────────────────────
// 무료 공개 API 어댑터 (기반 정보 축).
//
// 공통 규칙 세 가지:
//
//  1. <b>실패는 빈 목록이 아니다.</b> 예외·비정상 응답은 Error로 실어 보낸다.
//     조용히 빈 목록을 돌려주면 "마스터에 없다"와 "마스터를 못 받았다"가 구분되지 않고
//     대사 경보가 전부 거짓이 된다.
//  2. <b>서비스 키는 로그·오류·문서에 실리지 않는다.</b> Origin은 호스트까지만 적고,
//     오류 본문은 잘라서 담는다.
//  3. <b>기본은 전부 비활성.</b> 키가 없거나 설정이 꺼져 있으면 아예 등록되지 않으므로
//     테스트와 CI는 네트워크를 타지 않는다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>HTTP 출처 공통부 — 타임아웃, 예외 → Error 변환, 응답 본문 절단.</summary>
public abstract class HttpFoundationSource : IFoundationSource
{
    /// <summary>오류 메시지에 담는 응답 본문 길이 상한.</summary>
    protected const int ErrorSnippetLength = 200;

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    protected HttpFoundationSource(HttpClient http, TimeSpan? timeout = null)
    {
        _http = http;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Kind { get; }
    public abstract string Origin { get; }
    public abstract string License { get; }
    public bool RequiresNetwork => true;

    protected HttpClient Http => _http;

    public async Task<FoundationFetch> FetchAsync(FoundationQuery query, CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        try
        {
            var facts = await FetchFactsAsync(query, cts.Token);
            return new FoundationFetch(Id, facts, null, at);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FoundationFetch.Failed(Id, $"타임아웃 {_timeout.TotalSeconds:0}초 초과", at);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException)
        {
            return FoundationFetch.Failed(Id, $"{ex.GetType().Name}: {ex.Message}", at);
        }
    }

    /// <summary>출처별 조회. 예외를 던져도 된다 — 바깥에서 Error로 접힌다.</summary>
    protected abstract Task<IReadOnlyList<FoundationFact>> FetchFactsAsync(
        FoundationQuery query, CancellationToken ct);

    /// <summary>본문을 잘라 오류에 싣는다. 전량을 담으면 오류 로그가 응답 덤프가 된다.</summary>
    protected static string Snippet(string body) =>
        body.Length <= ErrorSnippetLength ? body : body[..ErrorSnippetLength] + "…";
}

/// <summary>
/// 기준환율 — open.er-api.com. <b>키가 필요 없는 무료 출처</b>라 설정만으로 켜진다.
/// 환율 자체는 구매 판단에 직접 쓰이지 않지만, 통화 코드가 실재하는지를 확인해 준다.
/// </summary>
public sealed class ExchangeRateSource : HttpFoundationSource
{
    private readonly string _baseCurrency;

    public ExchangeRateSource(HttpClient http, string baseCurrency = "KRW", TimeSpan? timeout = null)
        : base(http, timeout) => _baseCurrency = baseCurrency;

    public override string Id => "free.exchangerate";
    public override string Title => $"기준환율 ({_baseCurrency} 기준)";
    public override string Kind => FoundationKind.ExchangeRate;
    public override string Origin => "https://open.er-api.com";
    public override string License => "무료·키 불필요 (Open Exchange Rates API, 출처 표기 조건)";

    protected override async Task<IReadOnlyList<FoundationFact>> FetchFactsAsync(
        FoundationQuery query, CancellationToken ct)
    {
        var url = $"https://open.er-api.com/v6/latest/{Uri.EscapeDataString(_baseCurrency)}";
        var root = await Http.GetFromJsonAsync<JsonElement>(url, ct);

        if (root.TryGetProperty("result", out var result) &&
            result.GetString() is { } status && status != "success")
            throw new HttpRequestException($"result={status}");

        if (!root.TryGetProperty("rates", out var rates) || rates.ValueKind != JsonValueKind.Object)
            throw new JsonException("rates 객체가 없다");

        var asOf = root.TryGetProperty("time_last_update_unix", out var unix) && unix.TryGetInt64(out var secs)
            ? DateTimeOffset.FromUnixTimeSeconds(secs)
            : (DateTimeOffset?)null;

        var facts = new List<FoundationFact>();
        foreach (var rate in rates.EnumerateObject())
        {
            // 원문 표기를 그대로 싣는다 — double로 왕복시키면 0.00072가 0.00072000000000000005가 된다.
            var value = rate.Value.ValueKind == JsonValueKind.Number
                ? rate.Value.GetRawText()
                : rate.Value.ToString();

            var fact = FoundationFact.Create(Id, FoundationKind.ExchangeRate, rate.Name,
                label: $"{_baseCurrency}→{rate.Name}",
                attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["base"] = _baseCurrency,
                    ["rate"] = value,
                },
                asOf: asOf);
            if (fact is not null) facts.Add(fact);
        }

        return facts;
    }
}

/// <summary>
/// 공휴일 — 공공데이터포털(data.go.kr) 한국천문연구원 특일 정보.
/// 무료지만 <b>서비스 키가 필요</b>하다. 키가 없으면 이 출처는 등록되지 않는다.
///
/// <para>
/// 납기일이 공휴일인지는 관측으로 알 수 없다. 이 축이 필요한 이유가 여기 있다 —
/// "납기 2026-01-01"이 유효한 입력인지 판단하려면 달력이라는 외부 사실이 있어야 한다.
/// </para>
/// </summary>
public sealed class PublicHolidaySource : HttpFoundationSource
{
    private const string Endpoint =
        "https://apis.data.go.kr/B090041/openapi/service/SpcdeInfoService/getRestDeInfo";

    private readonly string _serviceKey;
    private readonly int _yearsAhead;

    public PublicHolidaySource(HttpClient http, string serviceKey, int yearsAhead = 1, TimeSpan? timeout = null)
        : base(http, timeout)
    {
        _serviceKey = serviceKey;
        _yearsAhead = Math.Clamp(yearsAhead, 0, 5);
    }

    public override string Id => "data.go.kr.holiday";
    public override string Title => "공휴일 (한국천문연구원 특일 정보)";
    public override string Kind => FoundationKind.Holiday;

    // 키는 Origin에 넣지 않는다 — Origin은 지식 문서 본문에 출처로 그대로 실린다.
    public override string Origin => "https://apis.data.go.kr (B090041 SpcdeInfoService)";
    public override string License => "공공데이터포털 무료 (서비스 키 필요, 이용허락범위 제한 없음)";

    protected override async Task<IReadOnlyList<FoundationFact>> FetchFactsAsync(
        FoundationQuery query, CancellationToken ct)
    {
        var facts = new List<FoundationFact>();
        var thisYear = DateTimeOffset.UtcNow.Year;

        for (var year = thisYear; year <= thisYear + _yearsAhead; year++)
        {
            var url = $"{Endpoint}?solYear={year}&numOfRows=100&_type=json" +
                      $"&ServiceKey={Uri.EscapeDataString(_serviceKey)}";

            using var response = await Http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode} {Snippet(body)}");

            // 키 오류·쿼터 초과 시 _type=json이어도 XML 오류 문서가 온다.
            if (!body.TrimStart().StartsWith('{'))
                throw new JsonException($"JSON이 아닌 응답: {Snippet(body)}");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("response", out var res)) continue;

            if (res.TryGetProperty("header", out var header) &&
                header.TryGetProperty("resultCode", out var code) &&
                code.GetString() is { } rc && rc != "00")
            {
                var message = header.TryGetProperty("resultMsg", out var m) ? m.GetString() : null;
                throw new HttpRequestException($"resultCode={rc} {message}");
            }

            foreach (var item in Items(res)) Add(facts, item, year);
        }

        return facts;
    }

    /// <summary>항목이 1건이면 배열이 아니라 객체로 온다 — 두 모양을 모두 받는다.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement response)
    {
        if (!response.TryGetProperty("body", out var body) ||
            !body.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Object ||
            !items.TryGetProperty("item", out var item))
            yield break;

        if (item.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in item.EnumerateArray()) yield return e;
        }
        else if (item.ValueKind == JsonValueKind.Object)
        {
            yield return item;
        }
    }

    private void Add(List<FoundationFact> facts, JsonElement item, int year)
    {
        if (!item.TryGetProperty("locdate", out var loc)) return;

        var raw = loc.ValueKind == JsonValueKind.Number ? loc.GetInt32().ToString() : loc.GetString();
        if (raw is not { Length: 8 }) return;

        var key = $"{raw[..4]}-{raw[4..6]}-{raw[6..]}";
        var name = item.TryGetProperty("dateName", out var dn) ? dn.GetString() : null;
        var isHoliday = item.TryGetProperty("isHoliday", out var h) ? h.GetString() ?? "N" : "N";

        var fact = FoundationFact.Create(Id, FoundationKind.Holiday, key, name,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["isHoliday"] = isHoliday,
                ["year"] = year.ToString(),
            });
        if (fact is not null) facts.Add(fact);
    }
}

/// <summary>
/// 사업자등록 상태 — 국세청 API (odcloud). 무료, 서비스 키 필요.
///
/// <para>
/// <b>목록형이 아니라 조회형 출처다.</b> 전체 사업자 명부를 주는 무료 API는 없으므로
/// 관측된 키를 질의로 넘겨 그것만 확인한다(<see cref="FoundationQuery"/>가 있는 이유).
/// </para>
/// <para>
/// 그리고 <b>관측 키가 사업자번호일 때만</b> 물어볼 수 있다. 화면에서 거래처가 "A사"로
/// 보이면 이 출처로는 검증할 수 없다 — 그 사실은 대사에서 "미등록"이 아니라
/// "대사 불가"로 보고돼야 한다. 키 공간이 다른 것을 불일치로 세면 경보가 무의미해진다.
/// </para>
/// </summary>
public sealed class BusinessStatusSource : HttpFoundationSource
{
    private const string Endpoint = "https://api.odcloud.kr/api/nts-businessman/v1/status";

    /// <summary>1회 요청 상한(API 규격). 넘으면 나눠 보낸다.</summary>
    public const int BatchSize = 100;

    private readonly string _serviceKey;

    public BusinessStatusSource(HttpClient http, string serviceKey, TimeSpan? timeout = null)
        : base(http, timeout) => _serviceKey = serviceKey;

    public override string Id => "data.go.kr.businessman";
    public override string Title => "사업자등록 상태 (국세청)";
    public override string Kind => FoundationKind.Company;
    public override string Origin => "https://api.odcloud.kr (nts-businessman/v1/status)";
    public override string License => "공공데이터포털 무료 (서비스 키 필요)";

    /// <summary>사업자등록번호 형태인가 — 숫자만 남겨 10자리. 아니면 물어볼 수 없다.</summary>
    public static string? AsBusinessNumber(string key)
    {
        var digits = new string(key.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 10 ? digits : null;
    }

    protected override async Task<IReadOnlyList<FoundationFact>> FetchFactsAsync(
        FoundationQuery query, CancellationToken ct)
    {
        var numbers = query.For(FoundationKind.Company)
            .Select(AsBusinessNumber)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (numbers.Count == 0) return Array.Empty<FoundationFact>();

        var facts = new List<FoundationFact>();
        var url = $"{Endpoint}?serviceKey={Uri.EscapeDataString(_serviceKey)}";

        for (var offset = 0; offset < numbers.Count; offset += BatchSize)
        {
            var batch = numbers.Skip(offset).Take(BatchSize).ToArray();
            using var response = await Http.PostAsJsonAsync(url, new { b_no = batch }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{(int)response.StatusCode} {Snippet(body)}");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                throw new JsonException($"data 배열이 없다: {Snippet(body)}");

            foreach (var row in data.EnumerateArray()) Add(facts, row);
        }

        return facts;
    }

    private void Add(List<FoundationFact> facts, JsonElement row)
    {
        if (!row.TryGetProperty("b_no", out var no) || no.GetString() is not { } bno) return;

        var status = row.TryGetProperty("b_stt", out var stt) ? stt.GetString() : null;

        // b_stt가 비면 국세청에 등록되지 않은 번호다 — 마스터에 넣지 않는다.
        // 여기서 넣어 버리면 미등록 거래처가 등록된 것으로 대사를 통과한다.
        if (string.IsNullOrWhiteSpace(status)) return;

        var fact = FoundationFact.Create(Id, FoundationKind.Company, bno, status,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["b_stt"] = status,
                ["tax_type"] = row.TryGetProperty("tax_type", out var t) ? t.GetString() ?? "" : "",
            });
        if (fact is not null) facts.Add(fact);
    }
}
