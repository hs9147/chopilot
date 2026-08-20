using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace ChoPilot.Mapping;

public sealed record VertexAiOptions(string ProjectId, string Location, string Model)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(Location) ||
            string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("Llm:Vertex:ProjectId, Location, Model은 vertex provider에 필수다");
    }
}

/// <summary>
/// Vertex AI Gemini REST 어댑터. 키 파일을 직접 읽지 않고 Google ADC 체인(Workload Identity,
/// Compute/GKE service account, GOOGLE_APPLICATION_CREDENTIALS, 개발자 ADC)을 사용한다.
/// </summary>
public sealed class VertexAiCompletionClient : ILlmCompletionClient
{
    public const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    private readonly HttpClient _http;
    private readonly VertexAiOptions _options;
    private readonly Func<CancellationToken, Task<string>> _accessToken;

    public VertexAiCompletionClient(HttpClient http, VertexAiOptions options,
        Func<CancellationToken, Task<string>>? accessToken = null)
    {
        options.Validate();
        _http = http;
        _options = options;
        _accessToken = accessToken ?? GetAdcAccessTokenAsync;
    }

    public async Task<LlmCompletion> CompleteAsync(
        string systemPrompt, string userPrompt, int maxOutputTokens,
        bool requireJsonObject, CancellationToken ct = default)
    {
        var endpoint = Endpoint(_options);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
                generationConfig = new
                {
                    temperature = 0,
                    maxOutputTokens,
                    responseMimeType = requireJsonObject ? "application/json" : "text/plain",
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _accessToken(ct));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Vertex AI returned {(int)response.StatusCode} ({response.ReasonPhrase})");
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public static async Task<string> GetAdcAccessTokenAsync(CancellationToken ct)
    {
        var credential = (await GoogleCredential.GetApplicationDefaultAsync(ct)).CreateScoped(CloudPlatformScope);
        if (credential.UnderlyingCredential is not ITokenAccess tokenAccess)
            throw new InvalidOperationException("Google ADC credential가 access token을 제공하지 않는다");
        return await tokenAccess.GetAccessTokenForRequestAsync(null, ct);
    }

    public static Uri Endpoint(VertexAiOptions options)
    {
        options.Validate();
        var host = options.Location.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? "https://aiplatform.googleapis.com"
            : $"https://{options.Location}-aiplatform.googleapis.com";
        return new Uri($"{host}/v1/projects/{Uri.EscapeDataString(options.ProjectId)}/locations/" +
                       $"{Uri.EscapeDataString(options.Location)}/publishers/google/models/" +
                       $"{Uri.EscapeDataString(options.Model)}:generateContent");
    }

    public static LlmCompletion Parse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var text = root.TryGetProperty("candidates", out var candidates) &&
                   candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0 &&
                   candidates[0].TryGetProperty("content", out var content) &&
                   content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array
            ? string.Concat(parts.EnumerateArray().Where(p => p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString()))
            : "";

        int? input = null, output = null;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            if (usage.TryGetProperty("promptTokenCount", out var prompt) && prompt.TryGetInt32(out var p)) input = p;
            if (usage.TryGetProperty("candidatesTokenCount", out var completion) && completion.TryGetInt32(out var c)) output = c;
        }
        return new LlmCompletion(text, input, output);
    }
}
