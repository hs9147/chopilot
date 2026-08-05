using System.Text;
using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>집계 실행 결과 — 무엇을 만들었고 무엇을 왜 걸렀는지.</summary>
public sealed record AggregationResult(
    List<KnowledgeDoc> Drafts,
    List<string> Skipped)          // 걸러진 이유 — 침묵하는 절단은 "다 훑었다"로 읽힌다
{
    public int DraftCount => Drafts.Count;
}

/// <summary>
/// 축별 신호 집계 → 지식 초안 (ARCHITECTURE §5.5 2단계).
///
/// <para>
/// <b>LLM이 없다.</b> 어떤 결핍이 몇 번, 몇 사람에게서 관측됐는지는 결정적으로 셀 수 있다.
/// AI는 3단계(서술)에서만 쓰이고, 이 집계가 그 입력을 만든다 — 그래서 비용이 관측 수가 아니라
/// 후보 수에 비례한다.
/// </para>
/// <para>
/// 두 개의 게이트가 항상 걸린다:
/// <b>지지도</b>(<see cref="MinSupport"/>) — 한 번은 우연이다(전이 그래프의 minCount와 같은 원칙).
/// <b>k인</b>(<see cref="MinDistinctUsers"/>) — 한 사람에게서만 나온 패턴을 org 문서로 올리면
/// 그 사람의 활동이 유출된다(§5.4 승격 사다리).
/// </para>
/// </summary>
public sealed class AxisAggregator
{
    public const int DefaultMinSupport = 3;
    public const int DefaultMinDistinctUsers = 2;

    private readonly UnknownConceptLog _unknownConcepts;
    private readonly UepStore _uep;
    private readonly SuggestionFeedbackStore _suggestions;
    private readonly KnowledgeStore _knowledge;
    private readonly EntityStore _entities;
    private readonly int _minSupport;
    private readonly int _minDistinctUsers;

    public AxisAggregator(
        UnknownConceptLog unknownConcepts, UepStore uep,
        SuggestionFeedbackStore suggestions, KnowledgeStore knowledge, EntityStore entities,
        int minSupport = DefaultMinSupport, int minDistinctUsers = DefaultMinDistinctUsers)
    {
        _unknownConcepts = unknownConcepts;
        _uep = uep;
        _suggestions = suggestions;
        _knowledge = knowledge;
        _entities = entities;
        _minSupport = minSupport;
        _minDistinctUsers = minDistinctUsers;
    }

    /// <summary>
    /// 도메인 축 초안 생성. 이미 존재하는 문서(대기·게시·<b>폐기</b>)는 다시 제안하지 않는다 —
    /// 사람이 폐기한 것을 집계기가 매번 되살리면 검수 큐가 무한 잔소리가 된다.
    /// </summary>
    public AggregationResult Aggregate(DateTimeOffset at)
    {
        var drafts = new List<KnowledgeDoc>();
        var skipped = new List<string>();

        AggregateUnknownConcepts(drafts, skipped, at);
        AggregateSharedTransitions(drafts, skipped, at);
        AggregateRejectedSuggestions(drafts, skipped, at);
        AggregateItemCharacteristics(drafts, skipped, at);

        return new AggregationResult(drafts, skipped);
    }

    /// <summary>
    /// 엔티티 공동 출현 → 품목 특성 초안 (품목 축).
    ///
    /// <para>
    /// <b>값이 실리는 유일한 축이다.</b> "M-001은 주로 A사에서 조달"이라고 써야 쓸모가 있는데
    /// M-001과 A사는 관측된 값이다. 그래서 게이트가 곧 프라이버시 경계다 — 한 사람에게서
    /// 한 번 관측된 것은 <b>레코드</b>이고, 여러 사람에게서 반복 관측된 공동 출현이라야
    /// <b>지식</b>이다. 민감 개념(단가·금액)은 마스킹으로 값이 서버에 도달하지 않으므로
    /// 구조적으로 여기 실릴 수 없다.
    /// </para>
    /// </summary>
    private void AggregateItemCharacteristics(List<KnowledgeDoc> drafts, List<string> skipped, DateTimeOffset at)
    {
        foreach (var link in _entities.Links())
        {
            // 지금은 품목↔거래처만 문서화한다. 다른 조합은 의미가 정해지기 전까지 보류.
            var (item, company) =
                (link.FromType, link.ToType) switch
                {
                    ("Item", "Company") => (link.FromKey, link.ToKey),
                    ("Company", "Item") => (link.ToKey, link.FromKey),
                    _ => (null, null),
                };
            if (item is null || company is null) continue;

            var id = $"note.item.{Slug(item)}";
            if (_knowledge.Get(id) is not null) { skipped.Add($"{id}: 이미 존재"); continue; }
            if (link.Count < _minSupport) { skipped.Add($"{id}: 공동 출현 {link.Count}회 < {_minSupport}"); continue; }
            if (link.DistinctUsers < _minDistinctUsers) { skipped.Add($"{id}: {link.DistinctUsers}명 < {_minDistinctUsers}명(k인 게이트)"); continue; }

            var body = new StringBuilder()
                .AppendLine($"품목 **{item}** 은 거래처 **{company}** 와 함께 {link.DistinctUsers}명에게서 {link.Count}회 관측됐다.")
                .AppendLine()
                .AppendLine("승인하면 이 품목의 통상 거래처로 기록되어 다음 작업 제안에 쓰인다.")
                .AppendLine("확인할 것: 실제로 주 거래처인가, 아니면 관측 기간에 우연히 몰린 것인가?")
                .ToString();

            drafts.Add(Draft(id, KnowledgeType.Note, $"품목 특성: {item}", body, at,
                new KnowledgeProvenance(new List<string> { "entity.cooccurrence" },
                    link.Count, link.DistinctUsers, link.LastSeen),
                axis: KnowledgeAxis.Item));
        }
    }

    /// <summary>거부된 보정 시도 → 개념 초안. 온톨로지 결핍의 가장 직접적인 증거다.</summary>
    private void AggregateUnknownConcepts(List<KnowledgeDoc> drafts, List<string> skipped, DateTimeOffset at)
    {
        foreach (var c in _unknownConcepts.Candidates())
        {
            var id = $"concept.{c.Term}";
            if (_knowledge.Get(id) is not null) { skipped.Add($"{id}: 이미 존재(대기/게시/폐기)"); continue; }
            if (c.Attempts < _minSupport) { skipped.Add($"{id}: 시도 {c.Attempts}회 < {_minSupport}"); continue; }
            if (c.DistinctUsers < _minDistinctUsers) { skipped.Add($"{id}: {c.DistinctUsers}명 < {_minDistinctUsers}명(k인 게이트)"); continue; }

            var body = new StringBuilder()
                .AppendLine($"보정 과정에서 **{c.Term}** 개념으로 {c.DistinctUsers}명이 {c.Attempts}회 정정을 시도했으나 온톨로지에 없어 거부됐다.")
                .AppendLine()
                .AppendLine($"- 관측된 업무객체: {string.Join(", ", c.BusinessObjects)}")
                .AppendLine($"- 마지막 시도: {c.LastSeen:yyyy-MM-dd HH:mm}")
                .AppendLine()
                .AppendLine("승인 전에 확인할 것: **민감 여부**(값이 마스킹돼야 하는가), 타입, 정규 개념명.")
                .AppendLine("집계기는 관측된 라벨을 그대로 이름으로 제안한다 — 정규명이 따로 있다면 재제출로 고쳐라.")
                .ToString();

            // 민감 여부를 모르는 개념은 sensitive: true로 제안한다 — 잘못 열어두는 쪽이
            // 잘못 닫아두는 쪽보다 훨씬 비싸다(값이 마스킹 없이 흐른다). 승인자가 내리면 된다.
            drafts.Add(Draft(
                id: id,
                type: KnowledgeType.Concept,
                title: $"개념 후보: {c.Term}",
                body: body,
                at: at,
                provenance: new KnowledgeProvenance(
                    new List<string> { "correction.unknown_concept" },
                    c.Attempts, c.DistinctUsers, c.LastSeen),
                concept: new Concept(c.Term, "string", new[] { c.Term }, Sensitive: true)));
        }
    }

    /// <summary>
    /// 여러 사용자가 공통으로 밟는 화면 전이 → 조직 업무 흐름 초안.
    /// 문서에는 <b>route</b>만 싣는다 — 화면 제목에는 레코드 식별자가 섞일 수 있어서
    /// org 스코프 문서에 들어가면 개인 활동이 새어 나간다.
    /// </summary>
    private void AggregateSharedTransitions(List<KnowledgeDoc> drafts, List<string> skipped, DateTimeOffset at)
    {
        var profiles = _uep.AllProfiles();
        var routeOf = profiles
            .SelectMany(p => p.Screens)
            .Where(s => !string.IsNullOrWhiteSpace(s.Route))
            .GroupBy(s => s.Signature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Route!, StringComparer.Ordinal);

        var edges = profiles
            .SelectMany(p => p.Transitions.Select(t => (User: p.UserId, T: t)))
            .Where(x => routeOf.ContainsKey(x.T.FromSignature) && routeOf.ContainsKey(x.T.ToSignature))
            .GroupBy(x => (From: routeOf[x.T.FromSignature], To: routeOf[x.T.ToSignature]))
            .Where(g => g.Key.From != g.Key.To);

        foreach (var g in edges)
        {
            var id = $"note.flow.{Slug(g.Key.From)}--{Slug(g.Key.To)}";
            if (_knowledge.Get(id) is not null) { skipped.Add($"{id}: 이미 존재"); continue; }

            var support = g.Sum(x => x.T.Count);
            var users = g.Select(x => x.User).Distinct(StringComparer.Ordinal).Count();
            if (support < _minSupport) { skipped.Add($"{id}: 관측 {support}회 < {_minSupport}"); continue; }
            if (users < _minDistinctUsers) { skipped.Add($"{id}: {users}명 < {_minDistinctUsers}명(k인 게이트)"); continue; }

            var median = g.Select(x => x.T.MedianGapSeconds).OrderBy(v => v).ElementAt(g.Count() / 2);
            var body = new StringBuilder()
                .AppendLine($"`{g.Key.From}` 다음에 `{g.Key.To}`로 이동하는 흐름이 {users}명에게서 {support}회 관측됐다.")
                .AppendLine()
                .AppendLine($"- 통상 간격: 약 {median:0}초")
                .AppendLine()
                .AppendLine("승인하면 조직 공용 업무 흐름으로 기록된다. 화면 제목이 아니라 route만 싣는다 — 제목에는 레코드 식별자가 섞일 수 있다.")
                .ToString();

            drafts.Add(Draft(id, KnowledgeType.Note, $"업무 흐름: {g.Key.From} → {g.Key.To}", body, at,
                new KnowledgeProvenance(new List<string> { "uep.transition" }, support, users,
                    g.Max(x => x.T.LastSeen))));
        }
    }

    /// <summary>
    /// 여러 사용자가 반복 거부한 제안 → 규칙 재검토 초안.
    /// 거부가 몰린다는 것은 사용자가 게으른 게 아니라 <b>규칙이 틀렸다</b>는 신호일 수 있다.
    /// </summary>
    private void AggregateRejectedSuggestions(List<KnowledgeDoc> drafts, List<string> skipped, DateTimeOffset at)
    {
        var rejected = _suggestions.Snapshot(int.MaxValue)
            .Where(r => r.Outcome == SuggestionOutcome.Rejected)
            .GroupBy(r => (r.BusinessObject, r.Subject));

        foreach (var g in rejected)
        {
            var id = $"note.rejected.{Slug(g.Key.BusinessObject)}.{Slug(g.Key.Subject)}";
            if (_knowledge.Get(id) is not null) { skipped.Add($"{id}: 이미 존재"); continue; }

            var users = g.Select(r => r.UserId).Distinct(StringComparer.Ordinal).Count();
            if (g.Count() < _minSupport) { skipped.Add($"{id}: 거부 {g.Count()}회 < {_minSupport}"); continue; }
            if (users < _minDistinctUsers) { skipped.Add($"{id}: {users}명 < {_minDistinctUsers}명(k인 게이트)"); continue; }

            var body = new StringBuilder()
                .AppendLine($"**{g.Key.Subject}** 관련 제안이 {g.Key.BusinessObject} 화면에서 {users}명에게 {g.Count()}회 거부됐다.")
                .AppendLine()
                .AppendLine($"예시 문구: \"{g.First().Text}\"")
                .AppendLine()
                .AppendLine($"검토할 것: 이 개념이 {g.Key.BusinessObject}의 필수 필드가 맞는가? 필수가 아니라면 규칙 문서(`rule.required.{g.Key.BusinessObject}`)에서 빼는 편이 낫다.")
                .ToString();

            drafts.Add(Draft(id, KnowledgeType.Note, $"반복 거부: {g.Key.BusinessObject} / {g.Key.Subject}", body, at,
                new KnowledgeProvenance(new List<string> { "suggestion.rejected" }, g.Count(), users,
                    g.Max(r => r.DecidedAt ?? r.ShownAt))));
        }
    }

    private static KnowledgeDoc Draft(
        string id, string type, string title, string body, DateTimeOffset at,
        KnowledgeProvenance provenance, Concept? concept = null,
        string axis = KnowledgeAxis.Domain) => new(
        Id: id,
        Axis: axis,
        Kind: KnowledgeKind.Curated,
        Type: type,
        Scope: "global",
        Title: title,
        Concept: concept,
        Required: null,
        Hint: null,
        Body: body,
        Version: 0,
        Status: KnowledgeStatus.PendingReview,
        Provenance: provenance,
        CreatedBy: "aggregator",
        ApprovedBy: null,
        UpdatedAt: at);

    /// <summary>문서 id에 쓸 안전한 조각. '/'와 '.'은 경로·네임스페이스 구분자라 접는다.</summary>
    private static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(c is '/' or '.' or ' ' or ':' ? '_' : c);
        return sb.ToString().Trim('_');
    }
}
