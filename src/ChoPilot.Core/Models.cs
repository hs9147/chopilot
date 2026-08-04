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
    PrivacyInfo Privacy);

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
    DateTimeOffset? LastInferredAt = null);

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
