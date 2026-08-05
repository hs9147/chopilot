using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>MCP 출처 설정. 도구 이름과 인자는 서버마다 다르므로 전부 설정으로 받는다.</summary>
public sealed record McpSourceOptions
{
    public required string Endpoint { get; init; }
    public required string Tool { get; init; }
    public string Kind { get; init; } = FoundationKind.Company;
    public string? BearerToken { get; init; }

    /// <summary>도구에 그대로 넘길 고정 인자 (JSON 객체 문자열). 비면 <c>{}</c>.</summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// 관측된 키를 실어 보낼 인자 이름. 설정하면 조회형 출처가 된다 —
    /// 전량 덤프가 없는 서버에서도 "우리가 본 것"만 물어볼 수 있다.
    /// </summary>
    public string? KeyArgument { get; init; }

    public string? License { get; init; }
}

/// <summary>
/// MCP 서버를 기반 정보 출처로 쓴다 (Streamable HTTP 전송, JSON-RPC 2.0).
///
/// <para>
/// 최소 클라이언트다: <c>initialize</c> → <c>notifications/initialized</c> → <c>tools/call</c>.
/// 공식 SDK를 끌어오지 않은 것은 의도다 — 이 경로에 필요한 건 도구 호출 하나뿐이고,
/// 서버 프로젝트에 프리뷰 의존성을 추가하면 CI가 패키지 복원에 묶인다.
/// </para>
/// <para>
/// <b>MCP 응답은 외부 데이터다.</b> 여기서 나온 사실은 마스터 조회 키와 개수로만 쓰이고,
/// 개념 문서(<see cref="KnowledgeType.Concept"/>)나 AI 프롬프트로는 <b>절대</b> 흘러가지 않는다.
/// LLM 편집자가 문자열만 돌려주게 만든 것과 같은 방어선이다: 외부에서 들어온 값이
/// <c>Sensitive</c>나 개념 이름을 정할 수 있으면 그게 곧 마스킹 우회 경로다.
/// </para>
/// </summary>
public sealed class McpFoundationSource : IFoundationSource
{
    /// <summary>한 번에 받아들일 사실 수 상한. 외부 서버가 무엇을 보내든 메모리는 우리가 정한다.</summary>
    public const int MaxFacts = 5000;

    private const string ProtocolVersion = "2025-06-18";

    private readonly HttpClient _http;
    private readonly McpSourceOptions _options;
    private readonly TimeSpan _timeout;
    private int _requestId;

    public McpFoundationSource(HttpClient http, McpSourceOptions options, TimeSpan? timeout = null)
    {
        _http = http;
        _options = options;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public string Id => $"mcp.{_options.Tool}";
    public string Title => $"MCP 도구 '{_options.Tool}'";
    public string Kind => _options.Kind;
    public string Origin => $"mcp:{_options.Endpoint}";
    public string License => _options.License ?? "MCP 서버 제공자 조건에 따름";
    public bool RequiresNetwork => true;

    public async Task<FoundationFetch> FetchAsync(FoundationQuery query, CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        try
        {
            var (init, sessionId) = await CallAsync("initialize", new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "chopilot", version = "1.0" },
            }, null, cts.Token);
            init.Dispose();

            await NotifyAsync("notifications/initialized", sessionId, cts.Token);

            using var result = (await CallAsync("tools/call", new
            {
                name = _options.Tool,
                arguments = BuildArguments(query),
            }, sessionId, cts.Token)).Response;

            return new FoundationFetch(Id, ParseFacts(result.RootElement, at), null, at);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FoundationFetch.Failed(Id, $"타임아웃 {_timeout.TotalSeconds:0}초 초과", at);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return FoundationFetch.Failed(Id, $"{ex.GetType().Name}: {ex.Message}", at);
        }
    }

    /// <summary>고정 인자에 관측 키를 얹는다. 키 인자가 없으면 목록형 도구로 취급한다.</summary>
    private Dictionary<string, JsonElement> BuildArguments(FoundationQuery query)
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(_options.Arguments))
        {
            using var doc = JsonDocument.Parse(_options.Arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Arguments는 JSON 객체여야 한다");
            foreach (var p in doc.RootElement.EnumerateObject()) args[p.Name] = p.Value.Clone();
        }

        if (_options.KeyArgument is { Length: > 0 } keyArg)
        {
            var keys = query.For(_options.Kind);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(keys));
            args[keyArg] = doc.RootElement.Clone();
        }

        return args;
    }

    private async Task<(JsonDocument Response, string? SessionId)> CallAsync(
        string method, object parameters, string? sessionId, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId);
        using var request = Envelope(new { jsonrpc = "2.0", id, method, @params = parameters }, sessionId);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{method}: {(int)response.StatusCode} {Truncate(body)}");

        var returnedSession = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault()
            : sessionId;

        var doc = JsonDocument.Parse(Payload(response.Content.Headers.ContentType, body));

        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            doc.Dispose();
            throw new InvalidOperationException($"{method}: {Truncate(error.ToString())}");
        }

        return (doc, returnedSession ?? sessionId);
    }

    private async Task NotifyAsync(string method, string? sessionId, CancellationToken ct)
    {
        using var request = Envelope(new { jsonrpc = "2.0", method }, sessionId);
        using var response = await _http.SendAsync(request, ct);
        // 알림은 응답 본문이 없다(202). 실패해도 tools/call에서 다시 드러나므로 여기서 막지 않는다.
    }

    private HttpRequestMessage Envelope(object body, string? sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

        // Streamable HTTP는 단건 JSON과 SSE 스트림 둘 다로 응답할 수 있다 — 둘 다 받는다.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (sessionId is { Length: > 0 }) request.Headers.Add("Mcp-Session-Id", sessionId);
        if (_options.BearerToken is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    /// <summary>SSE로 오면 마지막 data: 프레임이 JSON-RPC 응답이다.</summary>
    private static string Payload(MediaTypeHeaderValue? contentType, string body)
    {
        if (contentType?.MediaType is not "text/event-stream") return body;

        var last = body.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => l[5..].Trim())
            .LastOrDefault(l => l.Length > 0);

        return last ?? throw new JsonException("SSE 응답에 data 프레임이 없다");
    }

    /// <summary>
    /// 도구 결과의 텍스트 콘텐츠를 사실로 해석한다.
    /// 해석할 수 없으면 <b>빈 목록이 아니라 예외</b>다 — 조용한 0건은 마스터 공백과 구분되지 않는다.
    /// </summary>
    private IReadOnlyList<FoundationFact> ParseFacts(JsonElement root, DateTimeOffset at)
    {
        if (!root.TryGetProperty("result", out var result))
            throw new JsonException("result가 없다");

        if (result.TryGetProperty("isError", out var isError) &&
            isError.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"도구가 오류를 반환했다: {Truncate(result.ToString())}");

        var text = result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
                .Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Select(c => c.TryGetProperty("text", out var v) ? v.GetString() : null)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            : null;

        // structuredContent를 주는 서버는 그쪽이 정확하다 — 텍스트 재파싱보다 우선한다.
        var payload = result.TryGetProperty("structuredContent", out var structured)
            ? structured
            : text is not null
                ? JsonDocument.Parse(text).RootElement
                : throw new JsonException("텍스트 콘텐츠가 없다");

        var rows = Rows(payload) ?? throw new JsonException("사실 배열을 찾을 수 없다 (배열 또는 facts/items/data)");

        var facts = new List<FoundationFact>();
        foreach (var row in rows)
        {
            if (facts.Count >= MaxFacts) break;
            if (ToFact(row, at) is { } fact) facts.Add(fact);
        }

        return facts;
    }

    private static IEnumerable<JsonElement>? Rows(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array) return payload.EnumerateArray();
        if (payload.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in new[] { "facts", "items", "data", "results" })
            if (payload.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
                return array.EnumerateArray();

        return null;
    }

    private static readonly string[] KeyNames = { "key", "code", "id", "no", "b_no", "number", "name" };
    private static readonly string[] LabelNames = { "label", "name", "title", "description" };

    private FoundationFact? ToFact(JsonElement row, DateTimeOffset at)
    {
        if (row.ValueKind == JsonValueKind.String)
            return FoundationFact.Create(Id, _options.Kind, row.GetString()!, null, null, at);

        if (row.ValueKind != JsonValueKind.Object) return null;

        var key = First(row, KeyNames);
        if (key is null) return null;

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in row.EnumerateObject())
        {
            if (attributes.Count >= FoundationFact.MaxAttributes) break;
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
            attributes[p.Name] = p.Value.ToString();
        }

        return FoundationFact.Create(Id, _options.Kind, key, First(row, LabelNames), attributes, at);
    }

    private static string? First(JsonElement row, string[] names)
    {
        foreach (var name in names)
            if (row.TryGetProperty(name, out var value) &&
                value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

        return null;
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}
