using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>Vertex AI·Azure OpenAI가 공유하는 지식 초안 서술 어댑터.</summary>
public sealed class LlmKnowledgeEditor : IKnowledgeEditor
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

    private readonly ILlmCompletionClient _client;

    public LlmKnowledgeEditor(ILlmCompletionClient client) => _client = client;

    public async Task<string> DescribeAsync(KnowledgeDoc draft, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.CompleteAsync(SystemPrompt,
                BedrockKnowledgeEditor.Evidence(draft), 600, requireJsonObject: false, ct);
            return string.IsNullOrWhiteSpace(response.Text) ? draft.Body : response.Text.Trim();
        }
        catch when (!ct.IsCancellationRequested)
        {
            return draft.Body;
        }
    }
}
