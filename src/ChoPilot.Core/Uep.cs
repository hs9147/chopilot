namespace ChoPilot.Core;

/// <summary>사용자별 화면 사용 통계 (PHASE1-DESIGN §5 UEP: 사용 빈도/최근성).</summary>
public sealed record ScreenUsage(
    string Signature,
    string? Route,
    string? Title,
    int Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>
/// 화면 전이 1건 — "이 화면 다음에 저 화면으로 갔다"를 사용자별로 누적한 것.
///
/// <para>
/// 다음 작업을 제안하려면 <b>화면의 사용 빈도만으로는 부족하다</b>. 자주 여는 화면을 나열하는 것과
/// 지금 이 화면 다음에 무엇을 하는지는 다른 질문이고, 후자가 업무 흐름이다.
/// </para>
/// <para>
/// <see cref="MedianGapSeconds"/>는 평균이 아니라 중앙값이다 — 자리를 비웠다 돌아온 한 번의
/// 긴 간격이 평균을 통째로 끌어올리기 때문이다. 표본은 최근 것만 유한하게 보관한다.
/// </para>
/// </summary>
public sealed record ScreenTransition(
    string FromSignature,
    string ToSignature,
    string? ToTitle,
    int Count,
    double MedianGapSeconds,
    DateTimeOffset LastSeen);

/// <summary>
/// User Environment Profile (ARCHITECTURE §5.4 Personal Adaptive Plane, PHASE1-DESIGN §5).
/// 사용자별 격리(§8 멀티테넌시).
///  - <see cref="Screens"/>    : 무엇을 자주·최근에 쓰는가 (빈도·최근성)
///  - <see cref="Transitions"/>: 무엇 다음에 무엇을 하는가 (업무 흐름) → 다음 작업 제안의 입력
/// </summary>
public sealed record UserEnvironmentProfile(
    string UserId,
    IReadOnlyList<ScreenUsage> Screens,
    IReadOnlyList<ScreenTransition> Transitions);
