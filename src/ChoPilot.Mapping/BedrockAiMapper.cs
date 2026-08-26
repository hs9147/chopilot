using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>
/// Amazon Bedrock(Claude) 기반 동적 매핑 (ARCHITECTURE §5.2 step 4).
/// Anthropic Messages API 페이로드를 InvokeModel로 호출한다.
/// 자격증명·리전은 표준 AWS 체인(환경변수/프로파일/IAM 역할)에서 해석된다.
/// </summary>
public sealed class BedrockAiMapper : IAiMapper
{
    private readonly IAmazonBedrockRuntime _client;
    private readonly string _modelId;

    public BedrockAiMapper(IAmazonBedrockRuntime client, string modelId)
    {
        _client = client;
        // 현재 Anthropic 모델은 inference profile ID를 사용한다(ON_DEMAND 미지원).
        // 예: "us.anthropic.claude-haiku-4-5-20251001-v1:0"
        _modelId = modelId;
    }

    public async Task<MappingInference> InferAsync(
        string businessHint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
    {
        var payload = new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens = 1500,
            system = PromptBuilder.System(ontology),
            messages = new[]
            {
                new { role = "user", content = PromptBuilder.User(businessHint, tree) }
            }
        };

        var response = await _client.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = _modelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))
        }, ct);

        using var reader = new StreamReader(response.Body);
        var body = await reader.ReadToEndAsync(ct);
        return Parse(body, businessHint, ontology);
    }

    /// <summary>Bedrock 응답 → MappingInference. 모델 출력 텍스트에서 JSON 블록을 파싱.</summary>
    public static MappingInference Parse(string bedrockResponseJson, string businessHint, Concept[] ontology)
    {
        using var doc = JsonDocument.Parse(bedrockResponseJson);
        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "";

        // usage는 응답 최상위에 온다 — 매핑 파싱 성공 여부와 무관하게 비용은 발생하므로 먼저 읽는다.
        int? inputTokens = null, outputTokens = null;
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
        }

        return MappingInferenceParser.Parse(text, businessHint, ontology, inputTokens, outputTokens);
    }
}
