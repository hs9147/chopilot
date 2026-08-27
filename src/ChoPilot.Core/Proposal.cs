namespace ChoPilot.Core;

/// <summary>
/// 제안 종류. <b>지식 문서(온톨로지·규칙)와 겹치지 않는다</b> — 그쪽은 AxisAggregator가
/// 만들고 승인되면 시스템 동작이 바뀐다. 여기 있는 것은 <b>사람이 할 일</b>에 대한 제안이라
/// 승인해도 시스템은 그대로다. 둘을 한 통에 담으면 "승인"이 두 가지 뜻을 갖는다.
/// </summary>
public static class ProposalKind
{
    /// <summary>한 화면(route)이 여러 서명으로 갈렸다 — 표준화하면 캐시 적중률 상한이 올라간다.</summary>
    public const string ScreenSplit = "screen_split";

    /// <summary>같은 화면 전이를 반복한다 — 바로가기·일괄처리 후보.</summary>
    public const string WorkflowShortcut = "workflow_shortcut";

    /// <summary>A→B→A 되돌아오기가 잦다 — 앞 화면에 필요한 정보가 없다는 신호.</summary>
    public const string Rework = "rework";

    /// <summary>반복 관측되는데 기준정보에 없다 — 마스터 등록 또는 출처 연결이 필요하다.</summary>
    public const string MasterGap = "master_gap";

    /// <summary>한 화면에서 보정이 반복된다 — AI 판단이 그 화면에서 계속 틀린다.</summary>
    public const string CorrectionHotspot = "correction_hotspot";

    public static readonly string[] All =
        { ScreenSplit, WorkflowShortcut, Rework, MasterGap, CorrectionHotspot };
}

public static class ProposalStatus
{
    public const string Proposed = "proposed";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>
/// 사용자 평가. 한 숫자로 뭉치지 않는 이유는 <b>세 축이 서로 다른 결정을 이끌기 때문</b>이다.
///
/// <list type="bullet">
///   <item><see cref="Accuracy"/>가 낮다 = 생성기가 <b>없는 현상을 말한다</b>. 문턱을 올려도
///     소용없다 — 틀린 것 중 점수 높은 것이 남을 뿐이다. 그 종류를 꺼야 한다.</item>
///   <item><see cref="Usefulness"/>가 낮다 = 사실이지만 알 가치가 없다. 문턱을 올려 걸러낸다.</item>
///   <item><see cref="Actionability"/>가 낮다 = 맞고 유용하지만 우리가 못 한다.
///     <b>기준을 움직이지 않는다</b> — 조직 사정이지 생성기 품질이 아니다.
///     이걸 유용성과 뭉치면 옳게 찾아낸 종류가 조용히 꺼진다.</item>
/// </list>
///
/// <para>
/// 척도는 <b>0부터</b>다. 0은 "전혀 아니다"이고 1과 다른 판단이다 —
/// 1부터 시작하면 완전한 부정을 표현할 칸이 없어 최저점이 두 뜻을 겸한다.
/// </para>
/// </summary>
public sealed record ProposalRating(int Accuracy, int Usefulness, int Actionability)
{
    public const int Min = 0;
    public const int Max = 5;

    /// <summary>
    /// 문턱을 움직이는 합성 점수. <b>실행 가능성은 빠진다</b> — 못 하는 것과 틀린 것은 다르다.
    /// </summary>
    public double Quality => (Accuracy + Usefulness) / 2.0;

    public static bool IsValid(int value) => value is >= Min and <= Max;

    /// <summary>범위를 벗어난 축의 이름. 전부 정상이면 null.</summary>
    public string? Invalid()
    {
        if (!IsValid(Accuracy)) return "정확성";
        if (!IsValid(Usefulness)) return "유용성";
        if (!IsValid(Actionability)) return "실행 가능성";
        return null;
    }
}

/// <summary>
/// 제안 1건의 근거. <b>제안은 근거 없이 존재할 수 없다</b> —
/// 근거를 붙이지 않으면 읽는 사람이 검증할 수 없고, 그건 제안이 아니라 주장이다.
/// </summary>
public sealed record ProposalEvidence(
    int Occurrences,
    int DistinctUsers,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,

    /// <summary>근거를 되짚을 키(서명·route·개념 이름). 값은 담지 않는다.</summary>
    IReadOnlyList<string> Refs);

/// <summary>점수 1축. 총점만 남기면 왜 그 점수인지 되짚을 수 없다.</summary>
public sealed record ScoreDimension(string Name, double Value, double Weight, string Note);

public sealed record ProposalScore(double Total, IReadOnlyList<ScoreDimension> Dimensions);

/// <summary>
/// 종류별 게이트. 기준은 <b>종류마다 다르다</b> — 화면이 갈린 것은 구조적 사실이라
/// 한 사람의 관측으로도 참이지만, 업무 흐름 제안은 한 사람의 습관일 수 있다.
/// </summary>
public sealed record KindRule(
    string Kind,
    bool Enabled,
    int MinOccurrences,
    int MinDistinctUsers,
    double MinScore,

    /// <summary>꺼져 있다면 왜. 이유 없이 꺼진 규칙은 되살릴 근거도 없다.</summary>
    string? DisabledReason = null);

/// <summary>
/// 선정 기준. <b>버전이 붙고 이유가 남는다</b> — 기준이 조용히 바뀌면
/// "왜 저번엔 제안됐는데 이번엔 안 되나"에 답할 수 없다.
/// </summary>
public sealed record ProposalCriteria(
    int Version,
    DateTimeOffset UpdatedAt,
    string Rationale,
    double EvidenceWeight,
    double ReachWeight,
    double RecencyWeight,
    double ImpactWeight,
    IReadOnlyList<KindRule> Rules)
{
    /// <summary>종류별 문턱을 고치는 데 필요한 최소 평가 수. 이보다 적으면 건드리지 않는다.</summary>
    public const int MinRatingsToTune = 5;

    /// <summary>
    /// 이 아래로 정확성 평균이 떨어지면 그 종류를 끈다. 문턱을 올려도 소용없다 —
    /// <b>없는 현상을 말하는 것 중 점수 높은 것</b>이 남을 뿐이다.
    /// </summary>
    public const double MinAccuracy = 2.0;

    /// <summary>
    /// 축 가중치를 고치는 데 필요한 최소 평가 수. 문턱보다 높게 잡는다 —
    /// 가중치는 <b>모든 종류에 걸리는</b> 변경이라 적은 표본으로 움직이면 잡음이 전 종류로 번진다.
    /// </summary>
    public const int MinRatingsToTuneWeights = 8;

    /// <summary>한 번에 움직이는 가중치 폭. 크게 움직이면 다음 회차가 그걸 되돌리며 진동한다.</summary>
    public const double WeightStep = 0.05;

    public const double MinWeight = 0.05;
    public const double MaxWeight = 0.60;

    /// <summary>이만큼은 상관이 있어야 가중치를 건드린다. 약한 상관은 표본 잡음과 구분되지 않는다.</summary>
    public const double MinCorrelation = 0.3;

    /// <summary>최근성 감쇠의 반감기. 이보다 오래된 근거는 점수가 절반이 된다.</summary>
    public static readonly TimeSpan RecencyHalfLife = TimeSpan.FromDays(14);

    public KindRule? RuleFor(string kind) =>
        Rules.FirstOrDefault(r => string.Equals(r.Kind, kind, StringComparison.Ordinal));

    /// <summary>
    /// 시드 기준. 숫자는 추측이다 — 그래서 <see cref="Version"/> 1이고,
    /// 결정이 쌓이면 자체 평가가 관측으로 교체한다.
    /// </summary>
    public static ProposalCriteria Seed(DateTimeOffset at) => new(
        Version: 1,
        UpdatedAt: at,
        Rationale: "시드 — 아직 사람의 결정이 없어 추측한 값이다",
        EvidenceWeight: 0.35,
        ReachWeight: 0.25,
        RecencyWeight: 0.15,
        ImpactWeight: 0.25,
        Rules: new[]
        {
            // 화면이 갈린 것은 구조적 사실이라 한 사람이 봐도 참이다.
            new KindRule(ProposalKind.ScreenSplit, true, MinOccurrences: 4, MinDistinctUsers: 1, MinScore: 0.35),
            // 흐름·되돌아오기는 한 사람의 습관일 수 있다 — k인 게이트가 그 경계다.
            new KindRule(ProposalKind.WorkflowShortcut, true, MinOccurrences: 6, MinDistinctUsers: 2, MinScore: 0.40),
            new KindRule(ProposalKind.Rework, true, MinOccurrences: 4, MinDistinctUsers: 2, MinScore: 0.40),
            new KindRule(ProposalKind.MasterGap, true, MinOccurrences: 5, MinDistinctUsers: 2, MinScore: 0.35),
            new KindRule(ProposalKind.CorrectionHotspot, true, MinOccurrences: 3, MinDistinctUsers: 2, MinScore: 0.35),
        });
}

/// <summary>업무 개선 제안 1건. 승인해도 시스템 동작은 바뀌지 않는다 — 사람이 할 일이다.</summary>
public sealed record Proposal(
    string Id,
    string Kind,
    string Title,
    string Body,
    ProposalEvidence Evidence,
    ProposalScore Score,
    string Status,
    DateTimeOffset ProposedAt,

    /// <summary>제안 당시의 기준 버전. 기준이 바뀐 뒤 왜 이게 통과했는지 되짚는 고리다.</summary>
    int CriteriaVersion,

    DateTimeOffset? DecidedAt = null,
    string? DecidedBy = null,
    string? DecisionNote = null,

    /// <summary>
    /// 사용자 평가. <b>채택 여부와 별개다</b> — 근거는 맞지만 지금 손댈 수 없어 기각하는 일이
    /// 흔하고, 그걸 "쓸모없음"으로 세면 옳게 찾아낸 종류의 문턱이 올라간다.
    /// 기준을 고치는 유일한 입력이라 평가가 없으면 그 결정은 학습에 쓰이지 않는다.
    /// </summary>
    ProposalRating? Rating = null);

/// <summary>
/// 게이트에서 떨어진 후보와 그 이유. <b>버린 것을 말하지 않으면 "이게 전부"로 읽힌다</b> —
/// 제안이 0건일 때 근거가 없어서인지 기준이 높아서인지 구분되지 않는다.
/// </summary>
public sealed record SkippedCandidate(string Kind, string Key, string Reason, ProposalScore Score);

/// <summary>
/// 점수 계산. 순수 함수 — 저장소도 시계도 건드리지 않는다.
/// </summary>
public static class ProposalScoring
{
    /// <summary>
    /// 네 축을 0..1로 정규화해 가중 합산한다.
    ///
    /// <para>
    /// 정규화 기준을 <b>게이트 임계치의 2배</b>로 잡는다. 임계치를 겨우 넘긴 근거가 만점이 되면
    /// 점수가 게이트와 같은 말을 두 번 하게 되고, 그 위에서 순위를 매길 수 없다.
    /// </para>
    /// </summary>
    public static ProposalScore Score(
        ProposalCriteria criteria, KindRule rule, ProposalEvidence evidence,
        double impact, DateTimeOffset now)
    {
        var evidenceValue = Saturate(evidence.Occurrences, Math.Max(1, rule.MinOccurrences) * 2);
        var reachValue = Saturate(evidence.DistinctUsers, Math.Max(1, rule.MinDistinctUsers) * 2);

        // 오래된 근거는 지금의 업무를 말하지 않는다 — 반감기로 감쇠시킨다.
        var age = now - evidence.LastSeen;
        var recencyValue = age <= TimeSpan.Zero
            ? 1.0
            : Math.Pow(0.5, age.TotalDays / ProposalCriteria.RecencyHalfLife.TotalDays);

        var impactValue = Math.Clamp(impact, 0, 1);

        var dimensions = new[]
        {
            new ScoreDimension("근거", evidenceValue, criteria.EvidenceWeight,
                $"{evidence.Occurrences}회 관측 (기준 {rule.MinOccurrences})"),
            new ScoreDimension("도달", reachValue, criteria.ReachWeight,
                $"{evidence.DistinctUsers}명 (기준 {rule.MinDistinctUsers})"),
            new ScoreDimension("최근성", recencyValue, criteria.RecencyWeight,
                $"마지막 관측 {Math.Max(0, (int)age.TotalDays)}일 전"),
            new ScoreDimension("영향", impactValue, criteria.ImpactWeight, ImpactNote(impactValue)),
        };

        var weightSum = dimensions.Sum(d => d.Weight);
        var total = weightSum <= 0 ? 0 : dimensions.Sum(d => d.Value * d.Weight) / weightSum;
        return new ProposalScore(Math.Round(total, 4), dimensions);
    }

    private static double Saturate(int observed, int full) =>
        full <= 0 ? 1.0 : Math.Clamp((double)observed / full, 0, 1);

    /// <summary>
    /// 피어슨 상관. 표본이 얇거나 한쪽이 전부 같은 값이면 null —
    /// 0으로 돌려주면 "상관이 없다"로 읽히지만 실제로는 <b>말할 수 없다</b>는 뜻이다.
    /// </summary>
    public static double? Correlation(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        if (xs.Count != ys.Count || xs.Count < 3) return null;

        var meanX = xs.Average();
        var meanY = ys.Average();
        var sxx = xs.Sum(x => (x - meanX) * (x - meanX));
        var syy = ys.Sum(y => (y - meanY) * (y - meanY));
        if (sxx <= 1e-9 || syy <= 1e-9) return null;   // 한쪽이 상수 — 기울기를 말할 수 없다

        var sxy = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum();
        return sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>가중치 합을 1로 되돌린다. 정규화하지 않으면 총점의 눈금이 회차마다 달라진다.</summary>
    public static ProposalCriteria Normalize(ProposalCriteria criteria)
    {
        var sum = criteria.EvidenceWeight + criteria.ReachWeight
                  + criteria.RecencyWeight + criteria.ImpactWeight;
        if (sum <= 0) return criteria;
        return criteria with
        {
            EvidenceWeight = Math.Round(criteria.EvidenceWeight / sum, 3),
            ReachWeight = Math.Round(criteria.ReachWeight / sum, 3),
            RecencyWeight = Math.Round(criteria.RecencyWeight / sum, 3),
            ImpactWeight = Math.Round(criteria.ImpactWeight / sum, 3),
        };
    }

    private static string ImpactNote(double value) => value switch
    {
        >= 0.75 => "여러 사람의 반복 작업에 걸린다",
        >= 0.45 => "한 업무 흐름에 걸린다",
        _ => "국소적이다",
    };

    /// <summary>
    /// 게이트. 통과하면 null, 떨어지면 이유. <b>이유 문자열이 그대로 화면에 나간다</b> —
    /// "기준 미달"로 뭉뚱그리면 무엇을 더 모아야 하는지 알 수 없다.
    /// </summary>
    public static string? Reject(KindRule rule, ProposalEvidence evidence, ProposalScore score)
    {
        if (!rule.Enabled)
            return $"이 종류는 꺼져 있다 ({rule.DisabledReason ?? "이유 미기재"})";
        if (evidence.Occurrences < rule.MinOccurrences)
            return $"관측 {evidence.Occurrences}회 < 기준 {rule.MinOccurrences}회";
        if (evidence.DistinctUsers < rule.MinDistinctUsers)
            return $"{evidence.DistinctUsers}명 < 기준 {rule.MinDistinctUsers}명 (한 사람의 습관과 구분되지 않는다)";
        if (score.Total < rule.MinScore)
            return $"점수 {score.Total:0.00} < 기준 {rule.MinScore:0.00}";
        return null;
    }
}
