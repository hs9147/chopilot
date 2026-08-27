using System.Net;
using System.Text;
using System.Text.Json;
using ChoPilot.Core;
using ChoPilot.Mapping;
using Xunit;

namespace ChoPilot.Tests;

public class LlmProviderTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _response;
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        public CapturingHandler(string response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>실패 응답을 그대로 돌려주는 핸들러. 404 본문에 원인이 들어 있다.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FailingHandler(HttpStatusCode status, string body) => (_status, _body) = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    private static readonly UiNode Tree = new("root", "Form", "구매 요청", null, null, new()
    {
        new("material", "Edit", "품목 코드", "M-001", "txtMaterial", new()),
    });

    [Fact]
    public async Task Vertex_UsesAdcBearer_GenerateContent_AndParsesUsage()
    {
        var mapping = """{"business_object":"PurchaseRequest","fields":[{"element_ref":"material","concept":"Material","confidence":0.91}]}""";
        var handler = new CapturingHandler(JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text = mapping } } } } },
            usageMetadata = new { promptTokenCount = 123, candidatesTokenCount = 45 },
        }));
        using var http = new HttpClient(handler);
        var client = new VertexAiCompletionClient(http,
            new VertexAiOptions("my-project", "us-central1", "gemini-test"),
            _ => Task.FromResult("adc-token"));

        var result = await new CompletionClientAiMapper(client).InferAsync(
            "PurchaseRequest", Tree, ProcurementOntology.Concepts);

        Assert.Equal("PurchaseRequest", result.BusinessObject);
        Assert.Single(result.Fields);
        Assert.Equal("Material", result.Fields[0].Concept);
        Assert.Equal(123, result.InputTokens);
        Assert.Equal(45, result.OutputTokens);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("adc-token", handler.Request.Headers.Authorization.Parameter);
        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/my-project/locations/us-central1/publishers/google/models/gemini-test:generateContent",
            handler.Request.RequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("application/json", body.RootElement.GetProperty("generationConfig")
            .GetProperty("responseMimeType").GetString());
    }

    [Fact]
    public async Task AzureOpenAi_UsesDeploymentApiKey_AndParsesUsage()
    {
        var mapping = """{"business_object":"PurchaseRequest","fields":[{"element_ref":"material","concept":"Material","confidence":0.88}]}""";
        var handler = new CapturingHandler(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = mapping } } },
            usage = new { prompt_tokens = 77, completion_tokens = 19 },
        }));
        using var http = new HttpClient(handler);
        var client = new AzureOpenAiCompletionClient(http, new AzureOpenAiOptions(
            "https://contoso.openai.azure.com", "gpt-4o-prod", "2024-10-21", "azure-key", null));

        var result = await new CompletionClientAiMapper(client).InferAsync(
            "PurchaseRequest", Tree, ProcurementOntology.Concepts);

        Assert.Single(result.Fields);
        Assert.Equal(77, result.InputTokens);
        Assert.Equal(19, result.OutputTokens);
        Assert.Equal("azure-key", handler.Request!.Headers.GetValues("api-key").Single());
        Assert.Equal(
            "https://contoso.openai.azure.com/openai/deployments/gpt-4o-prod/chat/completions?api-version=2024-10-21",
            handler.Request.RequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format")
            .GetProperty("type").GetString());
    }

    [Fact]
    public void ProviderOptions_RejectMissingOrAmbiguousCredentials()
    {
        Assert.Throws<InvalidOperationException>(() => new VertexAiOptions("", "us-central1", "gemini").Validate());
        Assert.Throws<InvalidOperationException>(() => new AzureOpenAiOptions(
            "https://contoso.openai.azure.com", "deployment", "2024-10-21", "key", "token").Validate());
    }

    [Fact]
    public async Task AzureOpenAi_TextMode_DoesNotSendNullJsonResponseFormat()
    {
        var handler = new CapturingHandler("""{"choices":[{"message":{"content":"편집 결과"}}]}""");
        using var http = new HttpClient(handler);
        var client = new AzureOpenAiCompletionClient(http, new AzureOpenAiOptions(
            "https://contoso.openai.azure.com", "gpt-4o-prod", "2024-10-21", "azure-key", null));

        var result = await client.CompleteAsync("system", "user", 600, requireJsonObject: false);

        Assert.Equal("편집 결과", result.Text);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
    }

    // 404만 남기면 배포 이름이 틀린 것인지, api-version 이 그 경로를 모르는 것인지,
    // 엔드포인트가 다른 리소스인지 구분되지 않는다 — 그 답은 공급자가 본문에 적어 보낸다.
    [Fact]
    public async Task AzureFailure_CarriesTheResponseBody_NotJustTheStatus()
    {
        const string azure404 = """
            {"error":{"code":"DeploymentNotFound","message":"The API deployment for this resource does not exist."}}
            """;
        using var http = new HttpClient(new FailingHandler(HttpStatusCode.NotFound, azure404));
        var client = new AzureOpenAiCompletionClient(http,
            new AzureOpenAiOptions("https://example.openai.azure.com", "gpt-4o", "2024-10-21", "key", null));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CompleteAsync("s", "u", 100, requireJsonObject: true));

        Assert.Contains("404", ex.Message);
        Assert.Contains("DeploymentNotFound", ex.Message);          // 원인이 그대로 실린다
        Assert.Contains("Azure OpenAI", ex.Message);
        // 본문은 "그 배포가 없다"까지만 말한다 — 어떤 이름으로 물었는지는 URL에만 있다.
        Assert.Contains("/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21", ex.Message);
    }

    // Endpoint에 경로를 붙여 둔 흔한 오설정. 서명 검사만으로는 기동이 통과하고
    // 첫 관측에서야 404가 나므로, 그때 /openai 가 두 번 들어간 사실이 보여야 한다.
    [Fact]
    public async Task EndpointWithAPath_ShowsTheDoubledSegment_InThe404()
    {
        using var http = new HttpClient(new FailingHandler(HttpStatusCode.NotFound, "{}"));
        var client = new AzureOpenAiCompletionClient(http, new AzureOpenAiOptions(
            "https://example.openai.azure.com/openai/v1", "gpt-4o", "2024-10-21", "key", null));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CompleteAsync("s", "u", 100, requireJsonObject: true));

        Assert.Contains("/openai/v1/openai/deployments/gpt-4o/", ex.Message);
    }

    [Fact]
    public async Task VertexFailure_CarriesTheResponseBody()
    {
        const string vertex403 = """{"error":{"status":"PERMISSION_DENIED","message":"aiplatform.endpoints.predict denied"}}""";
        using var http = new HttpClient(new FailingHandler(HttpStatusCode.Forbidden, vertex403));
        var client = new VertexAiCompletionClient(http,
            new VertexAiOptions("p", "us-central1", "gemini-test"), _ => Task.FromResult("t"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CompleteAsync("s", "u", 100, requireJsonObject: true));

        Assert.Contains("PERMISSION_DENIED", ex.Message);
        Assert.Contains("locations/us-central1/publishers/google/models/gemini-test", ex.Message);
    }

    // 공급자가 HTML 오류 페이지를 돌려주는 경우가 있다 — 통째로 실으면 로그 한 줄이 화면을 덮는다.
    [Fact]
    public async Task LongErrorBodies_AreTruncated()
    {
        using var http = new HttpClient(new FailingHandler(
            HttpStatusCode.BadGateway, new string('x', 5000)));
        var client = new AzureOpenAiCompletionClient(http,
            new AzureOpenAiOptions("https://example.openai.azure.com", "d", "2024-10-21", "key", null));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CompleteAsync("s", "u", 100, requireJsonObject: true));

        Assert.True(ex.Message.Length < 600, $"길이 {ex.Message.Length}");   // 5000자를 그대로 싣지 않는다
        Assert.EndsWith("…", ex.Message);
    }

    [Fact]
    public async Task EmptyErrorBody_SaysSoInsteadOfTrailingColon()
    {
        using var http = new HttpClient(new FailingHandler(HttpStatusCode.ServiceUnavailable, ""));
        var client = new AzureOpenAiCompletionClient(http,
            new AzureOpenAiOptions("https://example.openai.azure.com", "d", "2024-10-21", "key", null));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CompleteAsync("s", "u", 100, requireJsonObject: true));

        Assert.Contains("응답 본문 없음", ex.Message);
    }
}
