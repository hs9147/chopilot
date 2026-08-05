using System.Security.Cryptography;
using System.Text;
using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>
/// 다음작업 힌트 1건. <paramref name="Id"/>는 제안의 <b>정체성</b>이고 <paramref name="Subject"/>는 그 대상 개념이다.
/// </summary>
public sealed record GuideHint(string Id, string Type, string Subject, string Text, bool Actionable);

/// <summary>Guide 조회 응답 (PHASE1-DESIGN §4.3). 읽기 전용 — 힌트는 실행 불가(Actionable=false).</summary>
public sealed record GuideResult(
    string BusinessObject,
    string Summary,
    Dictionary<string, string?> Fields,
    int Filled,
    int Required,
    double Ratio,
    List<GuideHint> NextHints,
    double Confidence,
    string Provenance);

/// <summary>
/// 현재 업무 요약 + 진행률 + 다음작업 힌트를 도출 (PHASE1-DESIGN §2.2 GuideService).
/// Phase 1은 가이드만 — 자동화(Actionable) 없음.
/// 필수 필드 규칙과 개념 별칭은 하드코딩이 아니라 <see cref="CompiledKnowledge"/>에서 온다 —
/// 규칙 문서가 게시되면 재배포 없이 가이드가 바뀐다(ARCHITECTURE §5.5).
/// </summary>
public static class GuideService
{
    public static GuideResult Build(MappingEntry entry, UiNode tree, BusinessObject bo, CompiledKnowledge knowledge)
    {
        var byRef = new Dictionary<string, UiNode>();
        Index(tree, byRef);

        // 규칙 미정의 업무객체는 매핑된 개념 전체를 필수로 간주(폴백).
        var required = knowledge.RequiredFor(entry.BusinessObject)
            ?? entry.Mapping.Select(m => m.Concept).Distinct().ToArray();

        // 값이 채워진 개념 (마스킹된 민감필드는 값이 있으므로 '채움'으로 계수, block만 제외)
        var filledConcepts = entry.Mapping
            .Where(m => byRef.TryGetValue(m.ElementRef, out var n) && !string.IsNullOrEmpty(n.Value))
            .Select(m => m.Concept)
            .ToHashSet();

        var filled = required.Count(filledConcepts.Contains);
        var req = required.Length;
        var ratio = req == 0 ? 0 : Math.Round((double)filled / req, 2);

        var hints = required
            .Where(c => !filledConcepts.Contains(c))
            .Select(c => new GuideHint(
                Id: SuggestionId(entry.BusinessObject, "guide", c),
                Type: "guide",
                Subject: c,
                Text: $"{Label(knowledge, c)} 입력이 남았습니다",
                Actionable: false))
            .ToList();

        var record = entry.RecordId?.Value is { Length: > 0 } v ? $" {v}" : "";
        var summary = $"{entry.BusinessObject}{record} 작업 중";

        return new GuideResult(entry.BusinessObject, summary, bo.Fields, filled, req, ratio,
            hints, bo.Confidence, bo.Provenance);
    }

    /// <summary>
    /// UEP 전이에서 다음 화면 제안을 만든다 (ARCHITECTURE §5.4 Personal Plane).
    ///
    /// <para>
    /// 화면 하나만 보고 만드는 힌트("납기 입력이 남았습니다")는 <b>지금 화면 안</b>의 빈칸을 가리킬 뿐이다.
    /// 다음 <b>작업</b>은 그 화면을 떠난 뒤에 있고, 그건 이 사용자가 실제로 어디로 갔는지에서만 나온다.
    /// </para>
    /// <para>
    /// 화면 제목이 없으면 서명 앞자리로 대체한다 — 사용자가 못 알아보는 제안은 수락도 거부도 될 수 없어
    /// 수락률을 무응답으로 오염시킨다.
    /// </para>
    /// </summary>
    public static GuideHint NextScreenHint(string businessObject, ScreenTransition transition)
    {
        var label = string.IsNullOrWhiteSpace(transition.ToTitle)
            ? ShortSignature(transition.ToSignature)
            : transition.ToTitle;

        return new GuideHint(
            Id: SuggestionId(businessObject, "next_screen", transition.ToSignature),
            Type: "next_screen",
            Subject: transition.ToSignature,
            Text: $"이 다음에는 보통 \"{label}\"(으)로 이동합니다 ({transition.Count}회, 약 {transition.MedianGapSeconds:0}초 후)",
            Actionable: false);   // Phase 1은 가이드만 — 이동까지 대신하지 않는다
    }

    private static string ShortSignature(string signature)
    {
        var body = signature.Split(':').Last();
        return body.Length <= 8 ? body : body[..8];
    }

    /// <summary>
    /// 제안 식별자. (업무객체, 종류, 대상)에서 <b>결정적으로</b> 유도한다 — 렌더마다 새로 뽑지 않는다.
    ///
    /// <para>
    /// 측정하려는 것이 "이 렌더가 클릭됐는가"가 아니라 "<b>이 제안이 쓸모 있는가</b>"이기 때문이다.
    /// 렌더마다 난수 ID를 발급하면 세션·사용자를 가로지르는 집계가 불가능해지고, 수락률이
    /// 화면 새로고침 횟수에 좌우된다. 노출의 맥락(누가·어느 관측에서 봤는지)은 ID가 아니라
    /// 노출 레코드가 들고 있다.
    /// </para>
    /// </summary>
    public static string SuggestionId(string businessObject, string type, string subject)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{businessObject}|{type}|{subject}"));
        return "sg:" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string Label(CompiledKnowledge knowledge, string concept) =>
        knowledge.ByName(concept)?.Aliases.FirstOrDefault() ?? concept;

    private static void Index(UiNode node, Dictionary<string, UiNode> map)
    {
        map[node.Ref] = node;
        foreach (var child in node.Children) Index(child, map);
    }
}
