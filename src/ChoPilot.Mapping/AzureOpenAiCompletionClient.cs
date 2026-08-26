using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChoPilot.Mapping;

public sealed record AzureOpenAiOptions(
    string Endpoint, string Deployment, string ApiVersion, string? ApiKey, string? BearerToken)
{
    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Llm:AzureOpenAI:Endpoint는 https 절대 URL이어야 한다");
        if (string.IsNullOrWhiteSpace(Deployment) || string.IsNullOrWhiteSpace(ApiVersion))
            throw new InvalidOperationException("Llm:AzureOpenAI:Deployment, ApiVersion은 azure_openai provider에 필수다");
        if (string.IsNullOrWhiteSpace(ApiKey) == string.IsNullOrWhiteSpace(BearerToken))
            throw new InvalidOperationException("Azure OpenAI 인증은 ApiKey 또는 BearerToken 중 정확히 하나여야 한다");
    }
}

/// <summary>Azure OpenAI deployment 경유 Chat Completions REST 어댑터.</summary>
public sealed class AzureOpenAiCompletionClient : ILlmCompletionClient
{
    private readonly HttpClient _http;
    private readonly AzureOpenAiOptions _options;

    public AzureOpenAiCompletionClient(HttpClient http, AzureOpenAiOptions options)
    {
        options.Validate();
        _http = http;
        _options = options;
    }

    public async Task<LlmCompletion> CompleteAsync(
        string systemPrompt, string userPrompt, int maxOutputTokens,
        bool requireJsonObject, CancellationToken ct = default)
    {
        // 일반 텍스트 편집 요청에 response_format:null을 보내면 일부 Azure API version이
        // schema 오류로 거부한다. JSON을 요구할 때만 해당 필드를 직렬화한다.
        object payload = requireJsonObject
            ? new
            {
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
                temperature = 0,
                max_tokens = maxOutputTokens,
                response_format = new { type = "json_object" },
            }
            : new
            {
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
                temperature = 0,
                max_tokens = maxOutputTokens,
            };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(_options))
        {
            Content = JsonContent.Create(payload),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Add("api-key", _options.ApiKey);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Azure OpenAI returned {(int)response.StatusCode} ({response.ReasonPhrase})");
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public static Uri Endpoint(AzureOpenAiOptions options)
    {
        options.Validate();
        return new Uri(options.Endpoint.TrimEnd('/') + "/openai/deployments/" +
                       Uri.EscapeDataString(options.Deployment) + "/chat/completions?api-version=" +
                       Uri.EscapeDataString(options.ApiVersion));
    }

    public static LlmCompletion Parse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var text = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
                   choices.GetArrayLength() > 0 && choices[0].TryGetProperty("message", out var message) &&
                   message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : "";
        int? input = null, output = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var p)) input = p;
            if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var c)) output = c;
        }
        return new LlmCompletion(text, input, output);
    }
}
