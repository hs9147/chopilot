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
}
