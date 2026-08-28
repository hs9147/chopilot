using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace ChoPilot.Mapping;

/// <summary>
/// Amazon Bedrock(Claude) chat completion 어댑터. Anthropic Messages API 페이로드를
/// <c>InvokeModel</c>로 호출한다. 자격증명·리전은 표준 AWS 체인(환경변수/프로파일/IAM 역할)이 푼다.
///
/// <para>
/// <b>다른 공급자와 같은 seam에 선다.</b> 예전에는 Bedrock만 <c>IAiMapper</c>·
/// <c>IKnowledgeEditor</c>를 각각 직접 구현해서, 프롬프트·재시도·오류 처리 개선이
/// Azure·Vertex에만 적용되고 Bedrock은 비껴가는 비대칭이 있었다. 여기로 모으면
/// <c>CompletionClientAiMapper</c>와 <c>LlmKnowledgeEditor</c>를 그대로 공유한다.
/// </para>
/// <para>
/// HTTP 상태로 실패를 말하는 다른 공급자와 달리 AWS SDK는 형식화된 예외를 던지므로
/// (<c>ResourceNotFoundException</c> 등) <c>LlmError</c>를 거치지 않는다 —
/// 이미 원인이 예외 타입과 메시지에 들어 있다.
/// </para>
/// </summary>
public sealed class BedrockCompletionClient : ILlmCompletionClient
{
    private readonly IAmazonBedrockRuntime _client;
    private readonly string _modelId;

    public BedrockCompletionClient(IAmazonBedrockRuntime client, string modelId)
    {
        _client = client;
        // 현재 Anthropic 모델은 inference profile ID를 사용한다(ON_DEMAND 미지원).
        // 예: "us.anthropic.claude-haiku-4-5-20251001-v1:0"
        _modelId = modelId;
    }

    public async Task<LlmCompletion> CompleteAsync(
        string systemPrompt, string userPrompt, int maxOutputTokens,
        bool requireJsonObject, CancellationToken ct = default)
    {
        // Anthropic Messages API에는 response_format이 없다. JSON 강제는 프롬프트가 하고
        // 파서가 텍스트에서 JSON 블록을 뽑는다 — requireJsonObject는 여기서 쓰지 않는다.
        var payload = new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens = maxOutputTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } },
        };

        var response = await _client.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = _modelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))),
        }, ct);

        using var reader = new StreamReader(response.Body);
        return Parse(await reader.ReadToEndAsync(ct));
    }

    /// <summary>
    /// Bedrock 응답 → 텍스트 + 토큰 사용량.
    /// <b>usage를 먼저 읽는다</b> — 본문 파싱이 실패해도 비용은 이미 발생했으므로
    /// 집계에서 누락되면 H6(매핑당 비용)이 실제보다 싸게 보인다.
    /// </summary>
    public static LlmCompletion Parse(string bedrockResponseJson)
    {
        using var doc = JsonDocument.Parse(bedrockResponseJson);
        var root = doc.RootElement;

        int? input = null, output = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var i)) input = i;
            if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var o)) output = o;
        }

        // 거부(stop_reason=refusal)나 빈 응답이면 content 자체가 없다 — 빈 텍스트로 돌려주면
        // 호출측이 각자의 폴백(매핑은 힌트 유지, 편집자는 원본 본문 유지)을 탄다.
        var text = root.TryGetProperty("content", out var content) &&
                   content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0 &&
                   content[0].TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? ""
            : "";

        return new LlmCompletion(text, input, output);
    }
}
