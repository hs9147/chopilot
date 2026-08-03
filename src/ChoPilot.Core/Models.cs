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
    string Status);        // "trusted" | "pending_review"

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
