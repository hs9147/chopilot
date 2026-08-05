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
    /// 관측을 일으킨 상호작용 — "focus_changed" | "structure_changed" | "save_clicked".
    /// <b>예약 필드</b>(ARCHITECTURE §11 작업 완료 신호): 지금 계약에 자리를 잡지 않으면
    /// 앞으로 캡처되는 스냅샷 전부가 완료 신호 없는 데이터로 남는다. 클라이언트 구현 전까지 null.
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
    int OntologyVersion = 0);

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
