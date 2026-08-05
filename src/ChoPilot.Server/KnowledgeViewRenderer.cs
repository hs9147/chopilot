using System.Text;
using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>
/// 사용자 축 지식 뷰 (ARCHITECTURE §5.4 Plane 3, axis=user).
///
/// <para>
/// <b>저장하지 않는다.</b> 뷰는 UEP·판단 스토어에서 결정적으로 재생성되는 파생물이므로,
/// 저장하면 즉시 낡고 "누가 이 페이지의 진실을 소유하는가"가 무너진다 — 통계의 진실은
/// 스토어에 남고 위키는 그 위의 서술 계층이다. 그래서 편집도 승인도 없고(Kind=view),
/// 컴파일 대상도 아니다(Type=note).
/// </para>
/// <para>
/// LLM은 여기 없다. 수치를 문장으로 바꾸는 일은 결정적으로 할 수 있고,
/// 관측당 비용이 붙는 자리에 AI를 두지 않는다는 원칙이 여기에도 적용된다.
/// </para>
/// </summary>
public sealed class KnowledgeViewRenderer
{
    private readonly UepStore _uep;
    private readonly SuggestionFeedbackStore _suggestions;

    public KnowledgeViewRenderer(UepStore uep, SuggestionFeedbackStore suggestions)
    {
        _uep = uep;
        _suggestions = suggestions;
    }

    /// <summary>이 사용자의 업무 프로파일 문서. 관측이 없으면 null.</summary>
    public KnowledgeDoc? Render(string userId, DateTimeOffset at)
    {
        var profile = _uep.Get(userId);
        if (profile is null) return null;

        var judgments = _suggestions.Snapshot(int.MaxValue)
            .Where(r => r.UserId == userId)
            .ToList();

        var body = new StringBuilder();
        body.AppendLine("## 자주 쓰는 화면");
        foreach (var s in profile.Screens.Take(5))
            body.AppendLine($"- {Label(s.Title, s.Route, s.Signature)} — {s.Count}회 (최근 {s.LastSeen:yyyy-MM-dd HH:mm})");

        if (profile.Transitions.Count > 0)
        {
            body.AppendLine();
            body.AppendLine("## 업무 흐름");
            foreach (var t in profile.Transitions.Take(5))
                body.AppendLine($"- → {t.ToTitle ?? Short(t.ToSignature)} — {t.Count}회, 보통 {t.MedianGapSeconds:0}초 후");
        }

        var accepted = judgments.Count(j => j.Outcome == SuggestionOutcome.Accepted);
        var rejected = judgments.Count(j => j.Outcome == SuggestionOutcome.Rejected);
        if (judgments.Count > 0)
        {
            body.AppendLine();
            body.AppendLine("## 제안에 대한 판단");
            body.AppendLine($"- 노출 {judgments.Count}건 중 수락 {accepted} / 거부 {rejected} / 무응답 {judgments.Count - accepted - rejected}");

            // 반복 거부는 이 사용자에게 그 제안을 그만 보여줄 근거다 — 수락만 모으면 놓친다.
            var repeatedlyRejected = judgments
                .Where(j => j.Outcome == SuggestionOutcome.Rejected)
                .GroupBy(j => j.Subject, StringComparer.Ordinal)
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var g in repeatedlyRejected)
                body.AppendLine($"- **{g.Key}** 제안을 {g.Count()}회 거부 — 이 사용자에겐 중단 후보");
        }

        return new KnowledgeDoc(
            Id: $"view.user.{userId}",
            Axis: KnowledgeAxis.User,
            Kind: KnowledgeKind.View,
            Type: KnowledgeType.Note,
            Scope: $"personal:{userId}",     // 본인만 조회(D5) — 화면 제목에 레코드가 섞일 수 있다
            Title: $"{userId}의 업무 프로파일",
            Concept: null, Required: null, Hint: null,
            Body: body.ToString().TrimEnd(),
            Version: 0,                      // 파생물이라 버전이 없다 — 스토어가 바뀌면 다음 렌더가 다르다
            Status: KnowledgeStatus.Published,
            Provenance: new KnowledgeProvenance(
                SignalRefs: new List<string> { "uep", "suggestions" },
                SupportCount: profile.Screens.Sum(s => s.Count),
                DistinctUsers: 1,
                LastObserved: profile.Screens.Max(s => s.LastSeen)),
            CreatedBy: "renderer",
            ApprovedBy: null,                // view는 승인 대상이 아니다
            UpdatedAt: at);
    }

    private static string Label(string? title, string? route, string signature) =>
        !string.IsNullOrWhiteSpace(title) ? title
        : !string.IsNullOrWhiteSpace(route) ? route
        : Short(signature);

    private static string Short(string signature)
    {
        var body = signature.Split(':').Last();
        return body.Length <= 8 ? body : body[..8];
    }
}
