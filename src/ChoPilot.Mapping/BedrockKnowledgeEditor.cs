using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>
/// Bedrock(Claude)로 지식 초안의 서술을 다듬는다 (ARCHITECTURE §5.5 3단계).
///
/// <para>
/// 집계기가 만든 본문은 정확하지만 건조하다 — 승인자가 판단하려면 무엇이 관측됐고
/// 무엇을 확인해야 하는지가 읽혀야 한다. 그 문장만 모델이 쓴다.
/// </para>
/// <para>
/// <b>실패해도 루프는 멈추지 않는다.</b> 호출이 실패하거나 빈 응답이면 집계기의 본문을
/// 그대로 돌려준다 — 서술 품질은 있으면 좋은 것이지 초안 생성의 전제가 아니다.
/// </para>
/// </summary>
public sealed class BedrockKnowledgeEditor : IKnowledgeEditor
{
    private const string SystemPrompt = """
        너는 구매 업무 지식베이스의 편집자다. 관측에서 자동 생성된 초안을 승인자가 읽고
        판단할 수 있는 문서로 다듬는다.

        규칙:
        - 주어진 근거에 없는 사실을 만들지 마라. 수치·횟수는 그대로 옮겨라.
        - 승인자가 확인해야 할 것(민감 여부·타입·정규명 등)을 빠뜨리지 마라.
        - 한국어 서술 6문장 이내. 마크다운 소제목(##)과 불릿(-)만 쓴다.
        - 개념의 민감 여부를 네가 판단하지 마라 — 승인자가 정한다. 확인하라고만 써라.
        - 본문만 출력한다. 머리말·따옴표·코드펜스 없이.
        """;

    private readonly IAmazonBedrockRuntime _client;
    private readonly string _modelId;

    public BedrockKnowledgeEditor(IAmazonBedrockRuntime client, string modelId)
    {
        _client = client;
        _modelId = modelId;
    }

    public async Task<string> DescribeAsync(KnowledgeDoc draft, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                anthropic_version = "bedrock-2023-05-31",
                max_tokens = 600,
                system = SystemPrompt,
                messages = new[] { new { role = "user", content = Evidence(draft) } },
            };

            var response = await _client.InvokeModelAsync(new InvokeModelRequest
            {
                ModelId = _modelId,
                ContentType = "application/json",
                Accept = "application/json",
                Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))),
            }, ct);

            using var reader = new StreamReader(response.Body);
            return Parse(await reader.ReadToEndAsync(ct)) ?? draft.Body;
        }
        catch when (!ct.IsCancellationRequested)
        {
            return draft.Body;   // 서술 실패가 초안 생성을 막지 않는다
        }
    }

    /// <summary>
    /// 편집자에게 주는 근거. <b>관측된 값은 넣지 않는다</b> — 문서에 값이 실리면 안 되는데
    /// 프롬프트에 값이 들어가면 모델이 그것을 본문에 옮겨 쓴다.
    /// </summary>
    public static string Evidence(KnowledgeDoc draft)
    {
        var sb = new StringBuilder()
            .AppendLine($"문서 종류: {draft.Type}")
            .AppendLine($"제목: {draft.Title}")
            .AppendLine($"관측 축: {draft.Axis}")
            .AppendLine($"지지도: {draft.Provenance.SupportCount}회, 서로 다른 사용자 {draft.Provenance.DistinctUsers}명")
            .AppendLine($"신호 출처: {string.Join(", ", draft.Provenance.SignalRefs)}");

        if (draft.Concept is { } c)
            sb.AppendLine($"제안된 개념: 이름 {c.Name}, 타입 {c.Type}, 별칭 {string.Join("/", c.Aliases)}, " +
                          $"민감 제안값 {(c.Sensitive ? "민감" : "비민감")}(승인자가 확정)");
        if (draft.Required is { } r)
            sb.AppendLine($"필수 필드 규칙: {r.BusinessObject} ← {string.Join(", ", r.Concepts)}");

        return sb.AppendLine().AppendLine("자동 생성된 초안 본문:").AppendLine(draft.Body).ToString();
    }

    /// <summary>Bedrock 응답에서 본문 텍스트만. 비면 null(호출측이 원본을 유지).</summary>
    public static string? Parse(string bedrockResponseJson)
    {
        using var doc = JsonDocument.Parse(bedrockResponseJson);
        if (!doc.RootElement.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
            return null;

        var text = content[0].TryGetProperty("text", out var t) ? t.GetString() : null;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
