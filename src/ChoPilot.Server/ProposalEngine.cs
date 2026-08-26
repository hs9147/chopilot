using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>게이트 이전의 제안 후보. 점수도 판정도 아직 없다.</summary>
internal sealed record Candidate(
    string Kind, string Key, string Title, string Body, ProposalEvidence Evidence, double Impact);

/// <summary>기준 조정 1건 — 무엇을 왜 바꿨는지.</summary>
public sealed record CriteriaChange(string Kind, string Field, double From, double To, string Reason);

/// <summary>자체 평가 결과. 바꾸지 않았어도 <b>왜 안 바꿨는지</b>가 남는다.</summary>
public sealed record TuningResult(
    int FromVersion, int ToVersion, IReadOnlyList<CriteriaChange> Changes, IReadOnlyList<string> Notes)
{
    public bool Changed => Changes.Count > 0;
}

public sealed record GenerationResult(
    IReadOnlyList<Proposal> Proposed,
    IReadOnlyList<SkippedCandidate> Skipped,
    int CriteriaVersion);

/// <summary>
/// 작업 이력에서 업무 개선 제안을 만든다 (ARCHITECTURE §5.5의 형제 — 지식이 아니라 <b>사람이 할 일</b>).
///
/// <para>
/// <b>LLM은 여기 없다.</b> 후보 산출도 선정도 결정적 집계다. 제안 문구까지 모델이 쓰면
/// 관측되지 않은 주장이 근거를 단 문장으로 섞여 들어오고, 읽는 사람은 그 둘을 구분할 수 없다.
/// 본문은 근거 숫자를 그대로 문장에 넣어 만든다.
/// </para>
/// <para>
/// 어느 후보도 <b>조용히 사라지지 않는다</b>. 게이트에 걸린 것은 이유와 함께 돌려준다 —
/// 제안 0건일 때 근거가 없어서인지 기준이 높아서인지 구분되어야 한다.
/// </para>
/// </summary>
public sealed class ProposalEngine
{
    private readonly ProposalStore _proposals;
    private readonly ObservationStore _observations;
    private readonly UepStore _uep;
    private readonly FoundationReconciler _reconciler;
    private readonly DecisionLog _decisions;
    private readonly Func<DateTimeOffset> _clock;

    public ProposalEngine(
        ProposalStore proposals, ObservationStore observations, UepStore uep,
        FoundationReconciler reconciler, DecisionLog decisions, Func<DateTimeOffset>? clock = null)
    {
        _proposals = proposals;
        _observations = observations;
        _uep = uep;
        _reconciler = reconciler;
        _decisions = decisions;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 자체 평가 — 사람의 결정에서 기준을 다시 뽑는다.
    ///
    /// <para>
    /// 표본이 얇을 때 건드리지 않는 것이 핵심이다. 두세 번 기각됐다고 종류를 끄면,
    /// 그 종류는 다시 제안되지 않으므로 <b>기각이 맞았는지 확인할 표본이 영원히 늘지 않는다</b>.
    /// </para>
    /// </summary>
    public TuningResult Tune()
    {
        var now = _clock();
        var criteria = _proposals.Current(now);
        var changes = new List<CriteriaChange>();
        var notes = new List<string>();
        var rules = criteria.Rules.ToList();

        foreach (var outcome in _proposals.Outcomes())
        {
            var index = rules.FindIndex(r => r.Kind == outcome.Kind);
            if (index < 0) continue;
            var rule = rules[index];

            if (outcome.Decided < ProposalCriteria.MinDecisionsToTune)
            {
                notes.Add($"{outcome.Kind}: 결정 {outcome.Decided}건 < {ProposalCriteria.MinDecisionsToTune}건 — 표본이 얇아 그대로 둔다");
                continue;
            }

            var rate = outcome.AcceptanceRate!.Value;

            if (rate < 0.25 && rule.MinScore < 0.8)
            {
                var to = Math.Round(Math.Min(0.8, rule.MinScore + 0.1), 2);
                rules[index] = rule with { MinScore = to };
                changes.Add(new CriteriaChange(outcome.Kind, "MinScore", rule.MinScore, to,
                    $"채택률 {rate:P0} ({outcome.Accepted}/{outcome.Decided}) — 문턱을 올린다"));
            }
            else if (rate < 0.25)
            {
                // 문턱을 이미 끝까지 올렸는데도 기각된다 — 점수가 아니라 종류가 틀렸다.
                rules[index] = rule with
                {
                    Enabled = false,
                    DisabledReason = $"문턱 {rule.MinScore:0.00}에서도 채택률 {rate:P0} ({outcome.Accepted}/{outcome.Decided})",
                };
                changes.Add(new CriteriaChange(outcome.Kind, "Enabled", 1, 0,
                    $"문턱을 올려도 채택되지 않는다 — 이 종류를 끈다"));
            }
            else if (rate > 0.7 && rule.MinScore > 0.2)
            {
                var to = Math.Round(Math.Max(0.2, rule.MinScore - 0.05), 2);
                rules[index] = rule with { MinScore = to };
                changes.Add(new CriteriaChange(outcome.Kind, "MinScore", rule.MinScore, to,
                    $"채택률 {rate:P0} ({outcome.Accepted}/{outcome.Decided}) — 문턱을 내려 더 올린다"));
            }
            else
            {
                notes.Add($"{outcome.Kind}: 채택률 {rate:P0} — 조정 구간(25%~70%) 안이라 그대로 둔다");
            }
        }

        if (changes.Count == 0)
            return new TuningResult(criteria.Version, criteria.Version, changes, notes);

        var revised = _proposals.Revise(criteria with
        {
            UpdatedAt = now,
            Rationale = string.Join(" · ", changes.Select(c => $"{c.Kind} {c.Field}: {c.Reason}")),
            Rules = rules,
        });
        return new TuningResult(criteria.Version, revised.Version, changes, notes);
    }

    /// <summary>후보 산출 → 점수 → 게이트 → 적재. 떨어진 것은 이유와 함께 함께 돌려준다.</summary>
    public GenerationResult Generate()
    {
        var now = _clock();
        var criteria = _proposals.Current(now);
        var proposed = new List<Proposal>();
        var skipped = new List<SkippedCandidate>();

        foreach (var candidate in Candidates(now))
        {
            var rule = criteria.RuleFor(candidate.Kind);
            if (rule is null)
            {
                skipped.Add(new SkippedCandidate(candidate.Kind, candidate.Key,
                    "이 종류의 기준이 없다", new ProposalScore(0, Array.Empty<ScoreDimension>())));
                continue;
            }

            var score = ProposalScoring.Score(criteria, rule, candidate.Evidence, candidate.Impact, now);
            var reject = ProposalScoring.Reject(rule, candidate.Evidence, score);
            if (reject is not null)
            {
                skipped.Add(new SkippedCandidate(candidate.Kind, candidate.Key, reject, score));
                continue;
            }

            var id = $"{candidate.Kind}:{candidate.Key}";
            var existing = _proposals.Get(id);
            if (existing is not null && existing.Status != ProposalStatus.Proposed)
            {
                skipped.Add(new SkippedCandidate(candidate.Kind, candidate.Key,
                    $"이미 {(existing.Status == ProposalStatus.Accepted ? "채택" : "기각")}됐다 — 다시 올리지 않는다", score));
                continue;
            }

            var proposal = new Proposal(
                Id: id,
                Kind: candidate.Kind,
                Title: candidate.Title,
                Body: candidate.Body,
                Evidence: candidate.Evidence,
                Score: score,
                Status: ProposalStatus.Proposed,
                ProposedAt: now,
                CriteriaVersion: criteria.Version);

            if (_proposals.Put(proposal)) proposed.Add(proposal);
        }

        return new GenerationResult(
            proposed.OrderByDescending(p => p.Score.Total).ToList(),
            skipped.OrderByDescending(s => s.Score.Total).ToList(),
            criteria.Version);
    }

    private IEnumerable<Candidate> Candidates(DateTimeOffset now) =>
        ScreenSplits()
            .Concat(TransitionCandidates())
            .Concat(MasterGaps())
            .Concat(CorrectionHotspots());

    // ── 화면이 갈렸다 ─────────────────────────────────────────────────────────
    // 같은 route가 여러 서명이면 방문마다 캐시 미스가 나므로 적중률에 구조적 상한이 생긴다.
    // 이건 사람의 습관이 아니라 화면의 성질이라 k인 게이트가 낮다.
    private IEnumerable<Candidate> ScreenSplits()
    {
        var stored = _observations.List();
        var byRoute = stored.GroupBy(s => SignatureService.NormalizeRoute(s.Event.Screen.Url));

        foreach (var route in byRoute)
        {
            var signatures = route
                .Select(s => SignatureService.Compute(s.Event.Screen, s.Event.Tree))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (signatures.Count < 2) continue;

            var users = route.Select(s => s.Event.UserId).Distinct(StringComparer.Ordinal).Count();
            var evidence = new ProposalEvidence(
                Occurrences: route.Count(),
                DistinctUsers: users,
                FirstSeen: route.Min(s => s.Event.CapturedAt),
                LastSeen: route.Max(s => s.Event.CapturedAt),
                Refs: signatures.Select(Short).ToList());

            // 갈린 수가 많을수록 적중률 상한이 낮다. 2갈래면 상한 50%, 4갈래면 25%.
            var impact = Math.Clamp(1.0 - 1.0 / signatures.Count, 0, 1);

            yield return new Candidate(
                ProposalKind.ScreenSplit,
                Key: route.Key,
                Title: $"{route.Key} 화면이 {signatures.Count}개 변형으로 갈린다",
                Body: $"같은 route를 {route.Count()}회 관측했는데 서명이 {signatures.Count}종이다"
                    + $" ({string.Join(", ", signatures.Take(4).Select(Short))}{(signatures.Count > 4 ? " 외" : "")})."
                    + $" 서명이 갈리면 방문마다 캐시 미스가 나므로 적중률은 구조적으로 최대 {1.0 / signatures.Count:P0}에 묶인다."
                    + " 화면 구조(요소 id·역할)를 통일하거나, 변형이 실제로 다른 화면이라면 route를 나눠야 한다.",
                Evidence: evidence,
                Impact: impact);
        }
    }

    // ── 업무 흐름 ─────────────────────────────────────────────────────────────
    // 전이는 사용자별로 쌓이므로 사람을 가로질러 합쳐야 "우리 팀의 흐름"이 된다.
    // 한 사람의 것만으로 제안하면 그 사람의 습관을 팀 규칙으로 만드는 셈이다 — k인 게이트가 그 경계다.
    private IEnumerable<Candidate> TransitionCandidates()
    {
        var edges = new Dictionary<(string From, string To), (int Count, HashSet<string> Users, DateTimeOffset Last, string? ToTitle)>();

        foreach (var profile in _uep.AllProfiles())
        foreach (var t in profile.Transitions)
        {
            var key = (t.FromSignature, t.ToSignature);
            if (!edges.TryGetValue(key, out var agg))
                agg = (0, new HashSet<string>(StringComparer.Ordinal), DateTimeOffset.MinValue, t.ToTitle);
            agg.Count += t.Count;
            agg.Users.Add(profile.UserId);
            if (t.LastSeen > agg.Last) agg.Last = t.LastSeen;
            agg.ToTitle ??= t.ToTitle;
            edges[key] = agg;
        }

        var titles = ScreenTitles();
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ((from, to), agg) in edges.OrderByDescending(e => e.Value.Count))
        {
            var fromName = titles.GetValueOrDefault(from) ?? Short(from);
            var toName = titles.GetValueOrDefault(to) ?? agg.ToTitle ?? Short(to);

            // 되돌아오기: A→B 와 B→A 가 둘 다 있으면 앞 화면에 필요한 정보가 없다는 뜻이다.
            if (edges.TryGetValue((to, from), out var back))
            {
                var pairKey = string.CompareOrdinal(from, to) < 0 ? $"{from}|{to}" : $"{to}|{from}";
                if (seenPairs.Add(pairKey))
                {
                    var cycles = Math.Min(agg.Count, back.Count);
                    var users = agg.Users.Intersect(back.Users, StringComparer.Ordinal).ToList();
                    yield return new Candidate(
                        ProposalKind.Rework,
                        Key: pairKey.Replace("|", "~", StringComparison.Ordinal),
                        Title: $"{fromName} ↔ {toName} 사이를 오간다",
                        Body: $"{fromName} → {toName} {agg.Count}회, 되돌아오기 {back.Count}회로 왕복이 {cycles}회 관측됐다."
                            + " 한쪽 화면을 보다가 다른 쪽 정보가 필요해 되돌아가는 형태다 —"
                            + " 필요한 항목을 앞 화면에 함께 보여주면 왕복이 사라진다.",
                        Evidence: new ProposalEvidence(
                            Occurrences: cycles,
                            DistinctUsers: users.Count,
                            FirstSeen: agg.Last < back.Last ? agg.Last : back.Last,
                            LastSeen: agg.Last > back.Last ? agg.Last : back.Last,
                            Refs: new[] { Short(from), Short(to) }),
                        Impact: Math.Clamp(cycles / 20.0, 0, 1));
                }
                continue;   // 왕복으로 잡힌 쌍은 단방향 제안으로 또 올리지 않는다
            }

            yield return new Candidate(
                ProposalKind.WorkflowShortcut,
                Key: $"{Short(from)}~{Short(to)}",
                Title: $"{fromName} 다음에 늘 {toName}로 간다",
                Body: $"{agg.Users.Count}명이 이 전이를 {agg.Count}회 반복했다."
                    + " 매번 손으로 찾아 들어가는 대신 앞 화면에서 바로가기를 두거나,"
                    + " 두 작업을 한 화면에서 처리할 수 있는지 검토할 만하다.",
                Evidence: new ProposalEvidence(
                    Occurrences: agg.Count,
                    DistinctUsers: agg.Users.Count,
                    FirstSeen: agg.Last,
                    LastSeen: agg.Last,
                    Refs: new[] { Short(from), Short(to) }),
                Impact: Math.Clamp(agg.Count / 30.0, 0, 1));
        }
    }

    // ── 기준정보 결손 ─────────────────────────────────────────────────────────
    // 반복 관측되는데 마스터에 없다. 다른 축과 방향이 반대라 관측으로 메울 수 없다 —
    // 사람이 마스터에 넣거나 출처를 붙여야 한다.
    private IEnumerable<Candidate> MasterGaps()
    {
        var report = _reconciler.Reconcile();

        foreach (var kind in report.Rows
                     .Where(r => r.Status is ReconcileStatus.Unmatched or ReconcileStatus.NoMaster)
                     .GroupBy(r => r.Kind))
        {
            var rows = kind.OrderByDescending(r => r.Mentions).ToList();
            var noMaster = rows.All(r => r.Status == ReconcileStatus.NoMaster);

            yield return new Candidate(
                ProposalKind.MasterGap,
                Key: kind.Key,
                Title: noMaster
                    ? $"{kind.Key} 기준정보 출처가 없다 — {rows.Count}종이 대사되지 못한다"
                    : $"{kind.Key} 미등록 {rows.Count}종이 반복 관측된다",
                Body: (noMaster
                        ? $"{kind.Key} 종류의 기반 출처가 붙어 있지 않아 관측된 값을 대사할 수 없다."
                        : $"마스터에 없는 {kind.Key} 값이 계속 관측된다.")
                    + $" 상위: {string.Join(", ", rows.Take(5).Select(r => $"{r.Key}({r.Mentions}회)"))}."
                    + (noMaster
                        ? " 무료 API나 MCP 출처를 Foundation 설정에 붙이면 대사가 성립한다."
                        : " 실재하는 값이면 마스터에 등록하고, 아니면 화면 입력을 막아야 한다."),
                Evidence: new ProposalEvidence(
                    Occurrences: rows.Sum(r => r.Mentions),
                    DistinctUsers: rows.Max(r => r.DistinctUsers),
                    FirstSeen: rows.Min(r => r.LastSeen),
                    LastSeen: rows.Max(r => r.LastSeen),
                    Refs: rows.Take(8).Select(r => r.Key).ToList()),
                Impact: Math.Clamp(rows.Count / 10.0, 0, 1));
        }
    }

    // ── 보정이 몰리는 화면 ────────────────────────────────────────────────────
    // 같은 화면에서 여러 사람이 반복해 고친다면 그 화면의 AI 판단이 계속 틀리는 것이다.
    private IEnumerable<Candidate> CorrectionHotspots()
    {
        var titles = ScreenTitles();

        foreach (var group in _decisions.Snapshot(1000)
                     .Where(d => d.Action == "correction")
                     .GroupBy(d => d.Signature, StringComparer.Ordinal))
        {
            var rows = group.ToList();
            var name = titles.GetValueOrDefault(group.Key) ?? Short(group.Key);
            var users = rows.Select(r => r.Actor).Distinct(StringComparer.Ordinal).Count();

            yield return new Candidate(
                ProposalKind.CorrectionHotspot,
                Key: Short(group.Key),
                Title: $"{name}에서 보정이 {rows.Count}회 반복된다",
                Body: $"{users}명이 이 화면의 매핑을 {rows.Count}회 고쳤다."
                    + " 개인 보정은 고친 사람에게만 적용되므로 나머지 사람은 계속 틀린 판단을 받는다 —"
                    + " 검수 큐에서 승격해 공용 평면에 올리거나, 화면 요소에 안정적인 id를 부여해"
                    + " AI가 매번 같은 답을 내도록 하는 편이 낫다.",
                Evidence: new ProposalEvidence(
                    Occurrences: rows.Count,
                    DistinctUsers: users,
                    FirstSeen: rows.Min(r => r.At),
                    LastSeen: rows.Max(r => r.At),
                    Refs: new[] { Short(group.Key) }),
                Impact: Math.Clamp(users / 5.0, 0, 1));
        }
    }

    /// <summary>서명 → 사람이 읽는 이름. 해시를 그대로 두면 제안이 무슨 화면 이야기인지 알 수 없다.</summary>
    private Dictionary<string, string> ScreenTitles()
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stored in _observations.List())
        {
            var signature = SignatureService.Compute(stored.Event.Screen, stored.Event.Tree);
            var name = stored.Event.Screen.Title
                ?? SignatureService.NormalizeRoute(stored.Event.Screen.Url);
            if (!string.IsNullOrWhiteSpace(name)) titles[signature] = name;
        }
        return titles;
    }

    private static string Short(string signature)
    {
        var body = signature.StartsWith("sha256:", StringComparison.Ordinal) ? signature[7..] : signature;
        return body.Length <= 12 ? body : body[..12];
    }
}
