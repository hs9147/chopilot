namespace ChoPilot.Core;

/// <summary>사용자별 화면 사용 통계 (PHASE1-DESIGN §5 UEP: 사용 빈도/최근성).</summary>
public sealed record ScreenUsage(
    string Signature,
    int Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>
/// User Environment Profile (ARCHITECTURE §5.4 Personal Adaptive Plane, PHASE1-DESIGN §5).
/// Phase 1은 <b>축적만</b> — 도출(다음작업·자동화 개인화)은 Phase 3에서 본격화.
/// 사용자별 격리(§8 멀티테넌시).
/// </summary>
public sealed record UserEnvironmentProfile(
    string UserId,
    IReadOnlyList<ScreenUsage> Screens);
