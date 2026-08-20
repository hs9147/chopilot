using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>
/// 추론 결과. 토큰 사용량은 실제 AI 호출에서만 채워진다(스텁·캐시는 null) — H6 비용 측정용.
/// </summary>
public sealed record MappingInference(
    string BusinessObject,
    List<FieldMapping> Fields,
    int? InputTokens = null,
    int? OutputTokens = null);

/// <summary>공급자 독립적인 텍스트 생성 결과. 비용 계측은 매핑 파싱 성공 여부와 분리한다.</summary>
public sealed record LlmCompletion(string Text, int? InputTokens = null, int? OutputTokens = null);

/// <summary>
/// Chat/text 생성 공급자 어댑터. 모델별 wire format과 인증은 여기서 끝내고,
/// 매핑·지식 편집은 동일한 프롬프트/검증 경로를 공유한다.
/// </summary>
public interface ILlmCompletionClient
{
    Task<LlmCompletion> CompleteAsync(
        string systemPrompt, string userPrompt, int maxOutputTokens,
        bool requireJsonObject, CancellationToken ct = default);
}

/// <summary>캐시 미스 시 트리→개념 매핑을 추론 (ARCHITECTURE §5.2 step 4).</summary>
public interface IAiMapper
{
    Task<MappingInference> InferAsync(
        string businessHint,
        UiNode tree,
        Concept[] ontology,
        CancellationToken ct = default);
}

/// <summary>
/// Vertex AI·Azure OpenAI처럼 chat completion을 제공하는 공급자의 공통 매핑 어댑터.
/// 공급자를 바꿔도 PromptBuilder와 모델 출력 allowlist가 달라지지 않는다.
/// </summary>
public sealed class CompletionClientAiMapper : IAiMapper
{
    private readonly ILlmCompletionClient _client;

    public CompletionClientAiMapper(ILlmCompletionClient client) => _client = client;

    public async Task<MappingInference> InferAsync(
        string businessHint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
    {
        var response = await _client.CompleteAsync(
            PromptBuilder.System(ontology), PromptBuilder.User(businessHint, tree),
            maxOutputTokens: 1500, requireJsonObject: true, ct);
        return MappingInferenceParser.Parse(response.Text, businessHint, ontology,
            response.InputTokens, response.OutputTokens);
    }
}

/// <summary>공급자와 무관한 모델 텍스트 → 안전한 매핑 변환.</summary>
public static class MappingInferenceParser
{
    public static MappingInference Parse(
        string modelText, string businessHint, Concept[] ontology,
        int? inputTokens = null, int? outputTokens = null)
    {
        var json = ExtractJson(modelText);
        if (json is null) return new MappingInference(businessHint, new(), inputTokens, outputTokens);

        try
        {
            using var mapped = System.Text.Json.JsonDocument.Parse(json);
            var root = mapped.RootElement;
            var businessObject = root.TryGetProperty("business_object", out var b) &&
                                 b.ValueKind == System.Text.Json.JsonValueKind.String
                ? b.GetString() ?? businessHint
                : businessHint;

            var fields = new List<FieldMapping>();
            if (root.TryGetProperty("fields", out var arr) &&
                arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var field in arr.EnumerateArray())
                {
                    if (field.ValueKind != System.Text.Json.JsonValueKind.Object ||
                        !field.TryGetProperty("concept", out var conceptElement) ||
                        conceptElement.ValueKind != System.Text.Json.JsonValueKind.String ||
                        !field.TryGetProperty("element_ref", out var refElement) ||
                        refElement.ValueKind != System.Text.Json.JsonValueKind.String)
                        continue;

                    var concept = Array.Find(ontology, c => c.Name.Equals(
                        conceptElement.GetString(), StringComparison.OrdinalIgnoreCase));
                    if (concept is null) continue; // 모델이 만든 개념은 통과시키지 않는다.

                    var confidence = field.TryGetProperty("confidence", out var confidenceElement) &&
                                     confidenceElement.TryGetDouble(out var rawConfidence) &&
                                     double.IsFinite(rawConfidence)
                        ? Math.Clamp(rawConfidence, 0, 1)
                        : 0.5;
                    fields.Add(new FieldMapping(refElement.GetString() ?? "", concept.Name,
                        confidence, "ai", concept.Sensitive));
                }
            }
            return new MappingInference(businessObject, fields, inputTokens, outputTokens);
        }
        catch (System.Text.Json.JsonException)
        {
            return new MappingInference(businessHint, new(), inputTokens, outputTokens);
        }
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}

/// <summary>
/// 비-AI 베이스라인. 노드 Name을 온톨로지 alias와 매칭한다.
/// Phase 0에서 "AI가 alias 매칭 대비 얼마나 더 정확한가"를 측정하는 대조군으로 유용.
/// </summary>
public sealed class StubAiMapper : IAiMapper
{
    public Task<MappingInference> InferAsync(
        string businessHint, UiNode tree, Concept[] ontology, CancellationToken ct = default)
    {
        var fields = new List<FieldMapping>();
        Walk(tree);
        return Task.FromResult(new MappingInference(businessHint, fields));

        void Walk(UiNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                var label = node.Name.Trim().ToLowerInvariant();
                var concept = Array.Find(ontology, c =>
                    c.Aliases.Any(a => label.Contains(a.ToLowerInvariant())) ||
                    label.Contains(c.Name.ToLowerInvariant()));

                if (concept is not null)
                    fields.Add(new FieldMapping(node.Ref, concept.Name, 0.6, "stub", concept.Sensitive));
            }
            foreach (var child in node.Children) Walk(child);
        }
    }
}
