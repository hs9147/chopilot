namespace ChoPilot.Core;

/// <summary>정규화된 접근성 트리 노드. 값(Value)은 최소 수집하며 PrivacyGate 통과 후 전송된다.</summary>
public sealed record UiNode(
    string Ref,
    string Role,
    string? Name,
    string? Value,
    string? AutomationId,
    List<UiNode> Children)
{
    public static UiNode Empty(string @ref) => new(@ref, "Unknown", null, null, null, new());
}

public sealed record RecordHint(string Source, string Key, string? Value);

public sealed record ScreenInfo(string? Url, string? Title, RecordHint? RecordHint);

public sealed record PrivacyInfo(string PolicyVersion, List<string> MaskedRefs);

/// <summary>
/// 관측을 일으킨 상호작용 (ARCHITECTURE §11 작업 완료 신호).
///
/// <para>
/// <see cref="SaveClicked"/> 하나만 성격이 다르다. 나머지 둘은 "화면이 바뀌었다"는 사실이고,
/// 이것은 <b>"사용자가 이 작업을 끝냈다"</b>는 판단이다. 저장 시점의 화면 상태라야
/// "이 업무객체에 실제로 무엇이 필요한가"의 증거가 된다 — 작성 중간의 빈칸은
/// 아직 안 채운 것인지 필요 없는 것인지 구분되지 않는다.
/// </para>
/// </summary>
public static class ObservationTrigger
{
    public const string FocusChanged = "focus_changed";
    public const string StructureChanged = "structure_changed";
    public const string SaveClicked = "save_clicked";

    /// <summary>null은 유효하다 — 완료 신호를 붙이지 않는 클라이언트가 여전히 관측을 올릴 수 있다.</summary>
    public static bool IsValid(string? trigger) =>
        trigger is null or FocusChanged or StructureChanged or SaveClicked;

    public static bool IsCompletion(string? trigger) => trigger == SaveClicked;
}

/// <summary>Client → Server 이벤트 계약 (PHASE1-DESIGN §4.1).</summary>
public sealed record ObservationEvent(
    string EventId,
    string SessionId,
    string UserId,
    DateTimeOffset CapturedAt,
    ScreenInfo Screen,
    UiNode Tree,
    PrivacyInfo Privacy,

    /// <summary>
    /// 관측을 일으킨 상호작용 (<see cref="ObservationTrigger"/>).
    /// null이면 완료 신호가 없는 관측이다 — 지표에는 들어가지만 필수 필드 검증에는 쓰이지 않는다.
    /// </summary>
    string? Trigger = null);

/// <summary>화면 서명별 학습된 매핑 캐시 엔트리 (ARCHITECTURE §5, D5 scope 반영).</summary>
public sealed record MappingEntry(
    string Signature,
    string Scope,          // "global" | "org:<id>" | "personal:<userId>"
    string? UserId,
    string BusinessObject,
    RecordHint? RecordId,
    List<FieldMapping> Mapping,
    double Confidence,
    string Status,         // "trusted" | "pending_review"

    /// <summary>
    /// 이 서명에 대해 AI 추론을 마지막으로 시도한 시각. 사람이 만든 매핑(보정·승격)은 null.
    /// 저신뢰 매핑의 <b>재추론 백오프</b> 기준이다 — <see cref="Status"/>가 pending_review인 채로
    /// 남아 있어도 매 관측마다 다시 물어보지 않는다.
    /// </summary>
    DateTimeOffset? LastInferredAt = null,

    /// <summary>
    /// 추론 당시의 지식 버전 (<see cref="CompiledKnowledge.Version"/>).
    /// 버전이 다르면 백오프와 무관하게 재추론한다 — "재추론이 의미 있는 건 온톨로지가
    /// 바뀐 뒤"라는 백오프의 전제를 실제 사건으로 연결하는 고리다. 기본 0(시드 이전).
    /// </summary>
    int OntologyVersion = 0,

    /// <summary>사용자 피드백의 optimistic concurrency 기준.</summary>
    int Revision = 1,

    /// <summary>추가 전용 저널에서 personal 매핑 삭제를 복원하기 위한 tombstone.</summary>
    bool Deleted = false);

public sealed record FieldMapping(
    string ElementRef,
    string Concept,
    double Confidence,
    string Provenance,     // "ai" | "stub" | "cache"
    bool Sensitive);

/// <summary>매핑 적용 결과. 민감 필드는 제외된다.</summary>
public sealed record BusinessObject(
    string Type,
    Dictionary<string, string?> Fields,
    double Confidence,
    string Provenance);
