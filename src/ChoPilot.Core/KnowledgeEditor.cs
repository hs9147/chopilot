namespace ChoPilot.Core;

/// <summary>
/// 지식 초안의 <b>서술</b>을 다듬는다 (ARCHITECTURE §5.5 3단계 Draft).
///
/// <para>
/// 반환값이 문자열 하나뿐인 것은 의도다. 편집자는 <b>본문만</b> 바꾸고
/// 구조화 페이로드(개념명·<c>Sensitive</c>·필수 필드)는 건드리지 못한다 —
/// LLM이 <c>Sensitive=false</c>를 쓸 수 있으면 문서 편집이 마스킹 방어선으로 가는
/// 프롬프트 주입 경로가 된다. 페이로드는 결정적 집계기가 만든 것을 그대로 쓴다.
/// </para>
/// <para>
/// 여기가 이 시스템에서 AI가 쓰이는 <b>두 번째이자 마지막</b> 자리다(첫째는 매핑 추론).
/// 배치로 돌고 초안 1건당 1회이므로 비용이 관측 수가 아니라 후보 수에 비례한다.
/// </para>
/// </summary>
public interface IKnowledgeEditor
{
    Task<string> DescribeAsync(KnowledgeDoc draft, CancellationToken ct = default);
}

/// <summary>
/// 기본 편집자 — 집계기가 쓴 본문을 그대로 쓴다. <b>AI 호출 없음.</b>
/// 지식 형성 루프는 LLM 없이도 완결되며, LLM은 서술 품질만 올리는 선택 사항이다.
/// </summary>
public sealed class PassthroughKnowledgeEditor : IKnowledgeEditor
{
    public Task<string> DescribeAsync(KnowledgeDoc draft, CancellationToken ct = default) =>
        Task.FromResult(draft.Body);
}
