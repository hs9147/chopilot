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

    /// <summary>미등록 키를 본문에 이름으로 적는 최대 개수. 나머지는 개수로만 적는다.</summary>
    public const int MaxNamedGaps = 20;

    /// <summary>완료 시점에 이 비율 이상 채워져 있던 개념은 <b>필수</b>로 제안한다.</summary>
    public const double RequiredFillRate = 0.9;

    /// <summary>이 비율 이하로만 채워진 개념은 필수에서 <b>뺀다</b>. 그 사이는 판단 보류다.</summary>
    public const double OptionalFillRate = 0.5;

    private readonly UnknownConceptLog _unknownConcepts;
    private readonly UepStore _uep;
    private readonly SuggestionFeedbackStore _suggestions;
    private readonly KnowledgeStore _knowledge;
    private readonly EntityStore _entities;
    private readonly FoundationStore _foundation;
    private readonly FoundationReconciler _reconciler;
    private readonly CompletionStore _completions;
    private readonly int _minSupport;
    private readonly int _minDistinctUsers;

    public AxisAggregator(
        UnknownConceptLog unknownConcepts, UepStore uep,
        SuggestionFeedbackStore suggestions, KnowledgeStore knowledge, EntityStore entities,
        FoundationStore foundation, FoundationReconciler reconciler, CompletionStore completions,
        int minSupport = DefaultMinSupport, int minDistinctUsers = DefaultMinDistinctUsers)
    {
        _unknownConcepts = unknownConcepts;
        _uep = uep;
        _suggestions = suggestions;
        _knowledge = knowledge;
        _entities = entities;
        _foundation = foundation;
        _reconciler = reconciler;
        _completions = completions;
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
        AggregateRequiredFields(drafts, skipped, at);
        AggregateFoundationSources(drafts, skipped, at);
        AggregateFoundationGaps(drafts, skipped, at);

        return new AggregationResult(drafts, skipped);
    }

    /// <summary>
    /// 완료 신호 → 필수 필드 규칙 <b>개정</b> (도메인 축).
    ///
    /// <para>
    /// 다른 집계와 달리 <b>이미 존재해도 건너뛰지 않는다</b>. <c>rule.required.{BO}</c>는
    /// 시드로 이미 게시돼 있고, 이 집계의 목적이 바로 그 추측을 관측으로 교체하는 것이기
    /// 때문이다. 대신 두 가지로 잔소리를 막는다: 대기 중인 개정본이 있으면 쌓지 않고,
    /// 관측이 현재 규칙과 같으면 초안을 만들지 않는다.
    /// </para>
    /// <para>
    /// 판정은 <b>세 갈래</b>다. 높은 채움률은 필수로, 낮은 채움률은 비필수로 제안하고,
    /// 그 사이(<see cref="OptionalFillRate"/>~<see cref="RequiredFillRate"/>)는 <b>건드리지 않는다</b> —
    /// 애매한 증거로 규칙을 흔들면 가이드가 매 배치마다 말을 바꾸고, 그러면 사용자가
    /// 가이드를 믿지 않게 된다. 관측되지 않은 개념도 그대로 둔다(증거 없음 ≠ 불필요).
    /// </para>
    /// </summary>
    private void AggregateRequiredFields(List<KnowledgeDoc> drafts, List<string> skipped, DateTimeOffset at)
    {
        foreach (var stat in _completions.Stats())
        {
            var id = $"rule.required.{Slug(stat.BusinessObject)}";
            if (_knowledge.PendingDraft(id) is not null) { skipped.Add($"{id}: 개정본이 이미 검수 대기 중"); continue; }
            if (stat.Completions < _minSupport) { skipped.Add($"{id}: 완료 {stat.Completions}회 < {_minSupport}"); continue; }
            if (stat.DistinctUsers < _minDistinctUsers) { skipped.Add($"{id}: {stat.DistinctUsers}명 < {_minDistinctUsers}명(k인 게이트)"); continue; }

            var current = _knowledge.Current.RequiredFor(stat.BusinessObject) ?? Array.Empty<string>();
            var proposed = new SortedSet<string>(current, StringComparer.Ordinal);
            var evidence = new List<ConceptFillStat>();

            foreach (var concept in stat.Concepts)
            {
                // 한 번 본 개념의 채움률로 규칙을 바꾸지 않는다.
                if (concept.Observed < _minSupport) continue;

                evidence.Add(concept);
                if (concept.FillRate >= RequiredFillRate) proposed.Add(concept.Concept);
                else if (concept.FillRate <= OptionalFillRate) proposed.Remove(concept.Concept);
            }

            var added = proposed.Except(current, StringComparer.Ordinal).ToList();
            var removed = current.Except(proposed, StringComparer.Ordinal).ToList();
            if (added.Count == 0 && removed.Count == 0)
            {
                skipped.Add($"{id}: 관측이 현재 규칙과 일치 (완료 {stat.Completions}회)");
                continue;
            }

            var body = new StringBuilder()
                .AppendLine($"**{stat.BusinessObject}** 완료 {stat.Completions}건({stat.DistinctUsers}명)을 관측한 결과, 필수 필드 규칙을 아래로 개정할 것을 제안한다.")
                .AppendLine()
                .AppendLine("| 개념 | 화면에 있던 횟수 | 채워진 횟수 | 채움률 |")
                .AppendLine("|---|---|---|---|");

            foreach (var e in evidence.OrderByDescending(e => e.FillRate))
                body.AppendLine($"| {e.Concept} | {e.Observed} | {e.Filled} | {e.FillRate:P0} |");

            body.AppendLine();
            if (added.Count > 0) body.AppendLine($"- **추가**: {string.Join(", ", added)} — 저장 시점에 거의 항상 채워져 있었다.");
            if (removed.Count > 0) body.AppendLine($"- **삭제**: {string.Join(", ", removed)} — 화면에 있었는데도 대체로 비어 있는 채로 저장됐다.");

            body.AppendLine()
                .AppendLine($"개정 후 필수 필드: {string.Join(", ", proposed)}")
                .AppendLine()
                .AppendLine($"채움률 {OptionalFillRate:P0}~{RequiredFillRate:P0} 구간의 개념은 증거가 애매해 손대지 않았다. 확인할 것: 조건부 필수(다른 필드 값에 따라 필요)인 개념이 섞여 있지 않은가?")
                .AppendLine("승인하면 가이드의 \"…입력이 남았습니다\" 제안이 즉시 바뀐다.");

            drafts.Add(Draft(id, KnowledgeType.RequiredFields,
                $"필수 필드 개정: {stat.BusinessObject}", body.ToString(), at,
                new KnowledgeProvenance(new List<string> { "observation.completion" },
                    stat.Completions, stat.DistinctUsers, stat.LastSeen),
                required: new RequiredFieldsRule(stat.BusinessObject, proposed.ToArray())));
        }
    }

    /// <summary>
    /// 기반 출처 등록 초안 (기반 축). 어떤 무료 API·MCP 서버를 우리 기준정보의 권위로
    /// 인정할 것인가 — 이건 관측이 아니라 <b>사람의 판단</b>이라 검수 큐를 거친다.
    ///
    /// <para>
    /// <b>k인 게이트를 걸지 않는다.</b> 그 게이트는 한 사람의 활동이 org 문서로 새는 것을
    /// 막는 장치인데, 이 문서에는 관측이 없다 — 엔드포인트·라이선스·건수뿐이다.
    /// 게이트를 기계적으로 걸면 출처 등록이 영원히 통과하지 못한다.
    /// </para>
    /// <para>
    /// 내장 출처는 초안이 되지 않는다. 승인이 묻는 것은 "이 <b>외부 당사자</b>를 믿는가"인데
    /// 내장 표준은 우리가 배포한 코드라 물을 상대가 없다 — 검수 큐에 넣으면 잡음만 는다.
    /// </para>
    /// </summary>
    private void AggregateFoundationSources(List<KnowledgeDoc> drafts, List<string> skipped, DateTimeOffset at)
    {
        foreach (var source in _foundation.Status())
        {
            var id = $"note.foundation.source.{Slug(source.Id)}";
            if (!source.RequiresNetwork) continue;   // 내장 출처엔 승인할 외부 당사자가 없다
            if (_knowledge.Get(id) is not null) { skipped.Add($"{id}: 이미 존재"); continue; }
            if (source.Error is { } error) { skipped.Add($"{id}: 조회 실패 — {error}"); continue; }
            if (source.Facts == 0) { skipped.Add($"{id}: 사실 0건 — 갱신 전이거나 응답이 비었다"); continue; }

            var body = new StringBuilder()
                .AppendLine($"**{source.Title}** 를 `{source.Kind}` 기반 정보의 출처로 등록한다.")
                .AppendLine()
                .AppendLine($"- 엔드포인트: `{source.Origin}`")
                .AppendLine($"- 이용 조건: {source.License}")
                .AppendLine($"- 현재 사실: {source.Facts}건")
                .AppendLine($"- 마지막 갱신: {(source.FetchedAt is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "내장(갱신 불필요)")}")
                .AppendLine()
                .AppendLine("승인은 \"이 출처를 우리 기준정보의 권위로 인정한다\"는 뜻이다. 확인할 것: 이용 조건이 사내 정책에 맞는가, 이 출처가 담당 업무의 관할과 일치하는가.")
                .ToString();

            drafts.Add(Draft(id, KnowledgeType.Note, $"기반 출처: {source.Title}", body, at,
                new KnowledgeProvenance(new List<string> { $"foundation.source:{source.Id}" },
                    source.Facts, 0, source.FetchedAt),
                axis: KnowledgeAxis.Foundation));
        }
    }

    /// <summary>
    /// 대사 결핍 초안 (기반 축) — 관측됐지만 마스터에 없는 키.
    ///
    /// <para>
    /// <b>미등록만 싣는다.</b> 마스터가 없거나(<c>no_master</c>) 키 공간이 어긋난 것
    /// (<c>unverifiable</c>)은 결핍이 아니라 대사 자체가 성립하지 않은 것이라
    /// 문서로 만들면 경보가 전량 거짓이 된다 — 그러면 사람이 경보를 보지 않게 된다.
    /// </para>
    /// <para>
    /// 값(거래처·품목 키)이 본문에 실리므로 품목 축과 같은 게이트를 건다. 한 사람에게서만
    /// 나온 미등록 키는 그 사람이 무엇을 다루는지 드러낸다 — 그건 레코드지 지식이 아니다.
    /// </para>
    /// </summary>
    private void AggregateFoundationGaps(List<KnowledgeDoc> drafts, List<string> skipped, DateTimeOffset at)
    {
        var report = _reconciler.Reconcile();

        foreach (var group in report.Rows
                     .Where(r => r.Status == ReconcileStatus.Unmatched)
                     .GroupBy(r => r.Kind, StringComparer.Ordinal))
        {
            var id = $"note.foundation.unmatched.{Slug(group.Key)}";
            if (_knowledge.Get(id) is not null) { skipped.Add($"{id}: 이미 존재"); continue; }

            var gaps = group
                .Where(r => r.Mentions >= _minSupport && r.DistinctUsers >= _minDistinctUsers)
                .OrderByDescending(r => r.Mentions)
                .ToList();

            if (gaps.Count == 0)
            {
                skipped.Add($"{id}: 게이트를 넘은 미등록 키가 없다 (지지도 {_minSupport}회 · {_minDistinctUsers}명)");
                continue;
            }

            var body = new StringBuilder()
                .AppendLine($"`{group.Key}` 마스터에 없는 값이 {gaps.Count}건 관측됐다.")
                .AppendLine();

            foreach (var gap in gaps.Take(MaxNamedGaps))
                body.AppendLine($"- `{gap.Key}` — {gap.DistinctUsers}명 / {gap.Mentions}회 (마지막 {gap.LastSeen:yyyy-MM-dd})");

            if (gaps.Count > MaxNamedGaps)
                body.AppendLine($"- 외 {gaps.Count - MaxNamedGaps}건");

            body.AppendLine()
                .AppendLine("확인할 것: 마스터 등록이 누락된 것인가, 화면 표기가 마스터와 다른 것인가, 아니면 애초에 거래해서는 안 되는 값인가?")
                .AppendLine("**이 목록은 마스터가 아니다** — 승인해도 기준정보가 되지 않고, 담당자가 확인할 대상으로만 남는다.");

            drafts.Add(Draft(id, KnowledgeType.Note, $"기반 대사: {group.Key} 미등록 {gaps.Count}건",
                body.ToString(), at,
                new KnowledgeProvenance(new List<string> { "foundation.reconcile" },
                    gaps.Sum(g => g.Mentions), gaps.Max(g => g.DistinctUsers),
                    gaps.Max(g => g.LastSeen)),
                axis: KnowledgeAxis.Foundation));
        }
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
        string axis = KnowledgeAxis.Domain, RequiredFieldsRule? required = null) => new(
        Id: id,
        Axis: axis,
        Kind: KnowledgeKind.Curated,
        Type: type,
        Scope: "global",
        Title: title,
        Concept: concept,
        Required: required,
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
