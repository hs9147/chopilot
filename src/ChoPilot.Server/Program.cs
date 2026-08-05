using Amazon;
using Amazon.BedrockRuntime;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;

// ─────────────────────────────────────────────────────────────────────────────
// ChoPilot.Server — Phase 1 Ingestion + Guide (읽기 전용)
//   POST /v1/observations : 관측 이벤트 → 서명 → 매핑 → Business Object 저장
//   GET  /v1/guide        : 현재 업무 요약 + 진행률 + 다음작업 힌트(Actionable=false)
//
// AI 매퍼는 설정으로 선택: 기본 StubAiMapper, UseBedrock=true 면 BedrockAiMapper.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// 클라이언트 JSON(PascalCase)과 record 생성자 파라미터 매칭
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);

builder.Services.AddSingleton<IMappingCache, InMemoryMappingCache>();
builder.Services.AddSingleton<ObservationStore>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<UepStore>();
builder.Services.AddSingleton<DecisionLog>();
builder.Services.AddSingleton<SuggestionFeedbackStore>();
builder.Services.AddSingleton<PersonalizationService>();

// Curated Knowledge Plane (ARCHITECTURE §5.4 Plane 3) — 온톨로지·규칙의 런타임 진실.
builder.Services.AddSingleton<KnowledgeStore>();
builder.Services.AddSingleton<UnknownConceptLog>();
builder.Services.AddSingleton<EntityStore>();
builder.Services.AddSingleton<KnowledgeViewRenderer>();

// ── 기반 정보 축 출처 (무료 API · MCP) ──────────────────────────────────────
// 등록 순서가 병합 우선순위다: 뒤에 등록된 출처가 앞을 덮는다.
// 내장 표준 → 키 없는 무료 API → 공공데이터포털(키 필요) → 조직이 지정한 MCP 서버.
// 네트워크 출처는 <b>기본 전부 비활성</b>이다 — 켜지 않으면 테스트도 CI도 밖으로 나가지 않는다.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IFoundationSource, EmbeddedFoundationSource>();

if (cfg.GetValue<bool>("Foundation:ExchangeRate:Enabled"))
    builder.Services.AddSingleton<IFoundationSource>(sp => new ExchangeRateSource(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("foundation"),
        cfg["Foundation:ExchangeRate:Base"] ?? "KRW"));

if (cfg["Foundation:Holiday:ServiceKey"] is { Length: > 0 } holidayKey)
    builder.Services.AddSingleton<IFoundationSource>(sp => new PublicHolidaySource(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("foundation"),
        holidayKey, cfg.GetValue<int?>("Foundation:Holiday:YearsAhead") ?? 1));

if (cfg["Foundation:BusinessStatus:ServiceKey"] is { Length: > 0 } bizKey)
    builder.Services.AddSingleton<IFoundationSource>(sp => new BusinessStatusSource(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("foundation"), bizKey));

foreach (var mcp in cfg.GetSection("Foundation:Mcp").GetChildren())
{
    if (mcp["Endpoint"] is not { Length: > 0 } endpoint || mcp["Tool"] is not { Length: > 0 } tool) continue;

    var options = new McpSourceOptions
    {
        Endpoint = endpoint,
        Tool = tool,
        Kind = mcp["Kind"] ?? FoundationKind.Company,
        BearerToken = mcp["BearerToken"],
        Arguments = mcp["Arguments"],
        KeyArgument = mcp["KeyArgument"],
        License = mcp["License"],
    };
    builder.Services.AddSingleton<IFoundationSource>(sp => new McpFoundationSource(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("foundation"), options));
}

builder.Services.AddSingleton(sp => new FoundationStore(sp.GetServices<IFoundationSource>()));
builder.Services.AddSingleton<FoundationReconciler>();

builder.Services.AddSingleton(sp => new AxisAggregator(
    sp.GetRequiredService<UnknownConceptLog>(),
    sp.GetRequiredService<UepStore>(),
    sp.GetRequiredService<SuggestionFeedbackStore>(),
    sp.GetRequiredService<KnowledgeStore>(),
    sp.GetRequiredService<EntityStore>(),
    sp.GetRequiredService<FoundationStore>(),
    sp.GetRequiredService<FoundationReconciler>(),
    cfg.GetValue<int?>("Knowledge:MinSupport") ?? AxisAggregator.DefaultMinSupport,
    cfg.GetValue<int?>("Knowledge:MinDistinctUsers") ?? AxisAggregator.DefaultMinDistinctUsers));
builder.Services.AddSingleton<IKnowledgeProvider>(sp => sp.GetRequiredService<KnowledgeStore>());
builder.Services.AddSingleton(sp => new KnowledgeService(
    sp.GetRequiredService<KnowledgeStore>(),
    sp.GetRequiredService<IMappingCache>(),
    cfg.GetValue<double?>("Mapping:ThetaHigh") ?? 0.8));

// 서버측 잔존 PII 스캔용(H4 반증). 마스킹 자체는 클라이언트가 이미 수행했다.
builder.Services.AddSingleton(_ => new PrivacyGate(policyVersion: cfg["Privacy:PolicyVersion"] ?? "1.0"));

if (cfg.GetValue<bool>("UseBedrock"))
{
    var region = RegionEndpoint.GetBySystemName(cfg["Aws:Region"] ?? "ap-northeast-2");
    builder.Services.AddSingleton<IAmazonBedrockRuntime>(_ => new AmazonBedrockRuntimeClient(region));
    // 현재 Anthropic 모델은 inference profile ID만 지원(ON_DEMAND base id → ResourceNotFoundException).
    builder.Services.AddSingleton<IAiMapper>(sp => new BedrockAiMapper(
        sp.GetRequiredService<IAmazonBedrockRuntime>(),
        cfg["Aws:BedrockModelId"] ?? "us.anthropic.claude-haiku-4-5-20251001-v1:0"));
}
else
{
    builder.Services.AddSingleton<IAiMapper, StubAiMapper>();
}

// 지식 초안 서술(§5.5 3단계). 기본은 AI 없음 — 루프는 LLM 없이도 완결된다.
// Knowledge:UseEditor=true 일 때만 배치에서 초안 1건당 1회 호출된다.
if (cfg.GetValue<bool>("UseBedrock") && cfg.GetValue<bool>("Knowledge:UseEditor"))
{
    builder.Services.AddSingleton<IKnowledgeEditor>(sp => new BedrockKnowledgeEditor(
        sp.GetRequiredService<IAmazonBedrockRuntime>(),
        cfg["Knowledge:EditorModelId"] ?? cfg["Aws:BedrockModelId"]
            ?? "us.anthropic.claude-haiku-4-5-20251001-v1:0"));
}
else
{
    builder.Services.AddSingleton<IKnowledgeEditor, PassthroughKnowledgeEditor>();
}

builder.Services.AddSingleton(sp => new MappingResolver(
    sp.GetRequiredService<IMappingCache>(),
    sp.GetRequiredService<IAiMapper>(),
    cfg["Mapping:OrgId"] ?? "default",
    cfg.GetValue<double?>("Mapping:ThetaHigh") ?? 0.8,
    // 저신뢰 매핑 재추론 백오프. 0으로 두면 θ 절벽이 되살아난다(관측마다 Bedrock 재호출).
    reinferAfter: cfg.GetValue<double?>("Mapping:ReinferAfterHours") is { } h
        ? TimeSpan.FromHours(h)
        : MappingResolver.DefaultReinferAfter));

var app = builder.Build();

// 측정 대시보드(wwwroot/index.html). PHASE0-MEASUREMENT의 jq/curl 절차를 화면으로 대체한다.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/observations",
    async (ObservationEvent evt, MappingResolver resolver, ObservationStore store,
           AuditService audit, UepStore uep, IKnowledgeProvider knowledgeProvider,
           EntityStore entities) =>
{
    // 서명→매핑→BO 구간을 계측한다. 캐시 미스(=Bedrock 호출)와 HIT의 차이가 여기서 드러난다.
    var started = System.Diagnostics.Stopwatch.GetTimestamp();

    // 관측 1건은 지식 스냅숏 하나로 처리한다 — 중간에 게시가 일어나도 반쯤 섞이지 않는다.
    var knowledge = knowledgeProvider.Current;
    var signature = SignatureService.Compute(evt.Screen, evt.Tree);
    var hint = knowledge.ResolveBusinessHint(evt.Screen);

    var res = await resolver.ResolveAsync(
        signature, evt.UserId, evt.Screen, evt.Tree, knowledge, hint);
    var bo = BusinessObjectBuilder.Build(res.Entry, evt.Tree);

    var durationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    store.Put(evt.EventId, evt, res.Entry, bo);
    audit.Record(evt, signature, res, durationMs);   // 불변 감사(Exit #4) + 지표 원자료

    // 개인화 축적(Exit ⑤, D5). 시각은 서버 수신 시각 — 클라이언트 시계에 좌우되면 안 된다.
    // route·title은 UEP를 사람이 읽을 수 있게 만든다: 해시만 남으면 다음작업 제안을 문장으로 쓸 수 없다.
    uep.RecordVisit(evt.UserId, signature, DateTimeOffset.UtcNow,
        SignatureService.NormalizeRoute(evt.Screen.Url), evt.Screen.Title);

    // 엔티티 결정 1단(§6 Deterministic). BO의 비민감 값만 보므로 단가·금액은 애초에 도달하지 않는다.
    entities.Record(EntityResolver.Extract(bo, knowledge), evt.UserId, signature, DateTimeOffset.UtcNow);

    return Results.Ok(new
    {
        observation_id = evt.EventId,
        signature,
        status = "accepted",
        cache_hit = res.CacheHit,
        source = res.Source,          // trusted_cache | deferred_cache | ai — 미스와 AI 호출은 다르다
        business_object = res.Entry.BusinessObject,
        confidence = res.Entry.Confidence
    });
});

app.MapGet("/v1/guide",
    (string observation_id, ObservationStore store, SuggestionFeedbackStore suggestions, UepStore uep,
     IKnowledgeProvider knowledgeProvider) =>
{
    var rec = store.Get(observation_id);
    if (rec is null) return Results.NotFound(new { error = "unknown observation_id" });

    var guide = GuideService.Build(rec.Entry, rec.Event.Tree, rec.BusinessObject, knowledgeProvider.Current);

    // 화면 안의 빈칸 힌트 뒤에 "이 화면 다음에 무엇을 하는가"를 붙인다.
    // 화면 하나만 보면 다음 '작업'이 아니라 다음 '입력칸'까지만 말할 수 있다(UEP 전이, D5).
    var next = uep.NextScreens(rec.Event.UserId, rec.Entry.Signature, limit: 2)
        .Select(t => GuideService.NextScreenHint(guide.BusinessObject, t));
    guide = guide with { NextHints = guide.NextHints.Concat(next).ToList() };

    // 제안이 사용자에게 나갔다는 사실을 남긴다 — 수락률의 분모(ARCHITECTURE §9).
    // 사용자는 헤더가 아니라 관측이 정한다: 가이드는 그 관측을 만든 사람의 것이다.
    suggestions.RecordImpressions(rec.Event.UserId, observation_id, rec.Entry.Signature,
        guide.BusinessObject, guide.NextHints, DateTimeOffset.UtcNow);

    return Results.Ok(guide);
});

// 감사 로그 조회(읽기 전용). 운영은 접근통제(IAM) 하에 노출.
app.MapGet("/v1/audit", (AuditService audit, int? limit) =>
    Results.Ok(new { count = audit.Count, entries = audit.Snapshot(limit ?? 100) }));

// H3b(캐시 적중률)·H6(지연 p95·AI 토큰) 자동 산출 — PHASE0-KIT 측정표의 원자료.
app.MapGet("/v1/metrics", (AuditService audit) => Results.Ok(audit.Metrics()));

// ── 측정 UI 조회 API (PHASE0-MEASUREMENT) ───────────────────────────────────
// 적재된 스냅샷 목록. H1 획득 집계·H2 식별·H4 마스킹을 한 줄로 요약한다.
app.MapGet("/v1/observations", (ObservationStore store, AuditService audit, PrivacyGate gate) =>
{
    var hits = audit.CacheHitByEventId();
    var items = store.List()
        .Select(s => MeasurementViews.Summarize(s, gate, hits.GetValueOrDefault(s.ObservationId)))
        .ToList();
    return Results.Ok(new { count = items.Count, items });
});

// 스냅샷 1건의 요소 인벤토리(PHASE0-KIT §2.1) + 적용된 매핑.
app.MapGet("/v1/observations/{id}", (string id, ObservationStore store, AuditService audit, PrivacyGate gate) =>
{
    var stored = store.Get(id);
    return stored is null
        ? Results.NotFound(new { error = "unknown observation_id" })
        : Results.Ok(MeasurementViews.Detail(stored, gate, audit.CacheHitByEventId().GetValueOrDefault(id)));
});

// 엔티티 결정 결과 (H5). splits는 같은 실체가 여러 키로 갈렸을 가능성 — 자동 병합하지 않는다.
app.MapGet("/v1/entities", (EntityStore entities) =>
{
    var splits = entities.Splits();
    return Results.Ok(new
    {
        count = entities.All().Count,
        splitCandidates = splits.Count,
        entities = entities.All(),
        links = entities.Links(),
        splits,
    });
});

// ── 기반 정보 축 (ARCHITECTURE §5.4 4축) ────────────────────────────────────
// 다른 축과 반대 방향이다: 관측이 지식을 만드는 게 아니라, 외부 출처가 마스터를 주고
// 관측이 그것에 대사된다. 관측을 마스터로 승격하는 경로는 없다.

app.MapGet("/v1/foundation", (FoundationStore foundation) =>
{
    var master = foundation.Master;
    return Results.Ok(new
    {
        master = new
        {
            count = master.Count,
            kinds = master.Kinds.Select(k => new { kind = k, count = master.CountOf(k) }),
        },
        sources = foundation.Status(),
    });
});

// 출처 갱신 — 유일하게 밖으로 나가는 호출이라 사용자를 요구하고 결정 이력에 남긴다.
// 관측된 키를 질의로 함께 넘긴다: 전량 덤프가 없는 무료 API는 "우리가 본 것"만 물어볼 수 있다.
app.MapPost("/v1/foundation/refresh",
    async (HttpRequest request, FoundationStore foundation, EntityStore entities,
           DecisionLog decisions, CancellationToken ct) =>
{
    if (RequestUser.From(request) is not { } actor)
        return Results.BadRequest(new { error = $"missing {RequestUser.Header} header" });

    var results = await foundation.RefreshAsync(FoundationStore.QueryFrom(entities), ct);
    var failed = results.Where(r => !r.Ok).ToList();

    decisions.Record("foundation_refresh", actor, "foundation", "global", 1.0,
        $"{results.Count}개 출처, 사실 {foundation.Master.Count}건, 실패 {failed.Count}건");

    return Results.Ok(new
    {
        refreshed = results.Count,
        facts = foundation.Master.Count,
        failures = failed.Select(f => new { source = f.SourceId, error = f.Error }),
        sources = foundation.Status(),
    });
});

// 관측 ↔ 마스터 대사. unmatched만 경보다 — no_master·unverifiable은 대사가 성립하지 않은 것이다.
app.MapGet("/v1/foundation/reconcile", (FoundationReconciler reconciler) =>
    Results.Ok(reconciler.Reconcile()));

// route별 서명 그룹핑 — 같은 화면이 여러 서명으로 갈렸는지가 캐시 적중률 미달의 1차 원인.
app.MapGet("/v1/signatures", (ObservationStore store) =>
{
    var routes = MeasurementViews.DiagnoseRoutes(store.List());
    return Results.Ok(new { splitRoutes = routes.Count(r => r.Split), routes });
});

// ── 개인화 · HITL (D5, ARCHITECTURE §5.2 step 6 / §5.4) ─────────────────────
// 개인 스코프의 읽기·쓰기는 요청 헤더의 사용자만 대상으로 한다. 본문·쿼리로 사용자를 받지 않는다.
// RequestUser는 인증이 아니다 — 실제 인증이 들어갈 자리다(RequestUser.cs 참조).

// 자기 UEP 조회 — 화면 사용 빈도/최근성(D5, Exit ⑤). 남의 프로파일은 조회할 수 없다.
app.MapGet("/v1/uep", (HttpRequest request, UepStore uep) =>
{
    if (RequestUser.From(request) is not { } userId)
        return Results.BadRequest(new { error = $"missing {RequestUser.Header} header" });

    var profile = uep.Get(userId);
    return profile is null
        ? Results.NotFound(new { error = "no profile for user" })
        : Results.Ok(profile);
});

// 보정 폼이 쓸 개념 목록. 사용자는 "UnitPrice"가 아니라 "단가"로 정정하므로 별칭까지 내려준다.
// 하드코딩이 아니라 게시된 지식의 컴파일 결과다 — 개념 문서가 승인되면 여기 즉시 나타난다.
app.MapGet("/v1/ontology", (IKnowledgeProvider knowledge) =>
    Results.Ok(new { version = knowledge.Current.Version, concepts = knowledge.Current.Concepts }));

// ── 지식 수명주기 (ARCHITECTURE §5.5, PHASE1-DESIGN §4.5) ───────────────────
// 제출 → 승인(게시) → 폐기. 삭제는 없다. 게시·폐기는 온톨로지·규칙을 영구히 바꾸는
// 판단이므로 헤더 사용자를 요구하고 결정 이력에 남긴다.

app.MapGet("/v1/knowledge",
    (HttpRequest request, KnowledgeStore store, KnowledgeViewRenderer views, string? axis, string? status) =>
{
    var user = RequestUser.From(request);
    var items = store.List(axis, status)
        // personal 스코프 문서는 본인만 본다(D5) — 헤더가 없으면 전부 제외
        .Where(d => !d.Scope.StartsWith("personal:", StringComparison.Ordinal) ||
                    d.Scope == $"personal:{user}")
        .ToList();

    // 사용자 축 뷰는 저장돼 있지 않다 — 요청 시 스토어에서 렌더한다(파생물이므로).
    if (user is not null && axis is null or KnowledgeAxis.User &&
        status is null or KnowledgeStatus.Published &&
        views.Render(user, DateTimeOffset.UtcNow) is { } view)
    {
        items.Insert(0, view);
    }

    return Results.Ok(new { version = store.Current.Version, count = items.Count, items });
});

app.MapGet("/v1/knowledge/{id}",
    (string id, HttpRequest request, KnowledgeStore store, KnowledgeViewRenderer views) =>
{
    var user = RequestUser.From(request);
    if (user is not null && id == $"view.user.{user}" &&
        views.Render(user, DateTimeOffset.UtcNow) is { } view)
        return Results.Ok(view);

    var doc = store.Get(id);
    if (doc is null) return Results.NotFound(new { error = "unknown knowledge id" });
    if (doc.Scope.StartsWith("personal:", StringComparison.Ordinal) &&
        doc.Scope != $"personal:{RequestUser.From(request)}")
        return Results.NotFound(new { error = "unknown knowledge id" });   // 존재 여부도 숨긴다
    return Results.Ok(doc);
});

app.MapPost("/v1/knowledge", (HttpRequest request, KnowledgeDoc doc, KnowledgeService svc) =>
{
    if (RequestUser.From(request) is not { } author)
        return Results.BadRequest(new { error = $"missing {RequestUser.Header} header" });

    var (draft, error) = svc.Submit(doc, author);
    return draft is null ? Results.BadRequest(new { error }) : Results.Ok(draft);
});

// 축별 집계 → 초안 생성 (ARCHITECTURE §5.5 1~2단계). 운영은 일 배치, 여기선 수동 트리거.
// LLM은 여기 없다 — 결정적 집계만으로 초안이 만들어지고, AI는 본문 품질 개선(3단계)에만 쓰인다.
app.MapPost("/v1/knowledge/aggregate",
    async (HttpRequest request, AxisAggregator aggregator, KnowledgeService svc,
           IKnowledgeEditor editor, bool? dryRun, CancellationToken ct) =>
{
    if (RequestUser.From(request) is not { } actor)
        return Results.BadRequest(new { error = $"missing {RequestUser.Header} header" });

    var result = aggregator.Aggregate(DateTimeOffset.UtcNow);
    if (dryRun == true)
        return Results.Ok(new { dryRun = true, drafts = result.Drafts, skipped = result.Skipped });

    // 초안은 검수 큐로 들어갈 뿐 게시되지 않는다 — LLM/집계기 자동 게시는 없다.
    var submitted = new List<KnowledgeDoc>();
    var rejected = new List<string>();
    foreach (var draft in result.Drafts)
    {
        // 편집자는 본문만 바꾼다. 개념·민감 여부·규칙은 집계기가 만든 것을 그대로 쓴다 —
        // LLM이 페이로드를 쓸 수 있으면 문서 편집이 마스킹 방어선으로 가는 주입 경로가 된다.
        var body = await editor.DescribeAsync(draft, ct);
        var (doc, error) = svc.Submit(draft with { Body = body }, "aggregator");
        if (doc is not null) submitted.Add(doc); else rejected.Add($"{draft.Id}: {error}");
    }

    return Results.Ok(new
    {
        submitted = submitted.Count,
        drafts = submitted,
        skipped = result.Skipped.Concat(rejected).ToList(),
    });
});

// 집계 원자료 — 어떤 결핍이 관측됐는지(승인 판단의 근거).
app.MapGet("/v1/knowledge/signals", (UnknownConceptLog unknown, int? limit) =>
    Results.Ok(new
    {
        unknownConceptAttempts = unknown.Count,
        candidates = unknown.Candidates(),
        recent = unknown.Snapshot(limit ?? 50),
    }));

app.MapPost("/v1/knowledge/{id}/approve",
    (string id, HttpRequest request, KnowledgeService svc, DecisionLog decisions) =>
{
    if (RequestUser.From(request) is not { } approver)
        return Results.BadRequest(new { error = $"missing {RequestUser.Header} header" });

    var (doc, error) = svc.Approve(id, approver);
    if (doc is null) return Results.BadRequest(new { error });

    decisions.Record("knowledge_publish", approver, doc.Id, doc.Scope, 1.0,
        $"{doc.Type} v{doc.Version}: {doc.Title}");
    return Results.Ok(doc);
});

app.MapPost("/v1/knowledge/{id}/deprecate",
    (string id, HttpRequest request, KnowledgeService svc, DecisionLog decisions) =>
{
    if (RequestUser.From(request) is not { } actor)
        return Results.BadRequest(new { error = $"missing {RequestUser.Header} header" });

    var outcome = svc.Deprecate(id, actor);
    if (outcome.Doc is null) return Results.BadRequest(new { error = outcome.Error });

    decisions.Record("knowledge_deprecate", actor, outcome.Doc.Id, outcome.Doc.Scope, 0,
        $"{outcome.Doc.Type} v{outcome.Doc.Version}: {outcome.Doc.Title} (매핑 {outcome.TouchedMappings}건 정리)");
    return Results.Ok(new { doc = outcome.Doc, touchedMappings = outcome.TouchedMappings });
});

// 검수 큐(HITL) — 저신뢰 매핑 열람. 개인 스코프는 제외된다.
app.MapGet("/v1/review", (PersonalizationService svc, int? limit) =>
    Results.Ok(new { entries = svc.ReviewQueue(limit ?? 100) }));

// 검수 통과분을 trusted로 승격(HITL). 누가 승인했는지 결정 이력에 남긴다.
app.MapPost("/v1/review/promote",
    (HttpRequest request, PromoteRequest req, PersonalizationService svc, DecisionLog decisions) =>
{
    if (RequestUser.From(request) is not { } actor)
        return Results.BadRequest(new { error = $"missing {RequestUser.Header} header" });

    var promoted = svc.Promote(req);
    if (promoted is null) return Results.NotFound(new { error = "unknown signature/scope" });

    decisions.Record("promote", actor, promoted.Signature, promoted.Scope, promoted.Confidence,
        $"{promoted.BusinessObject}: {promoted.Mapping.Count} fields");
    return Results.Ok(promoted);
});

// 개인 보정 — 사용자 정정 매핑을 personal 스코프에 적재(피드백 루프, D5).
app.MapPost("/v1/correction",
    (HttpRequest request, CorrectionRequest req, PersonalizationService svc, DecisionLog decisions) =>
{
    if (RequestUser.From(request) is not { } userId)
        return Results.BadRequest(new { error = $"missing {RequestUser.Header} header" });

    var outcome = svc.ApplyCorrection(userId, req);
    if (!outcome.Accepted)
        return Results.BadRequest(new
        {
            error = "unknown concepts — 온톨로지 이름 또는 별칭만 허용된다",
            unknownConcepts = outcome.UnknownConcepts,
        });

    var entry = outcome.Entry!;
    decisions.Record("correction", userId, entry.Signature, entry.Scope, entry.Confidence,
        $"{entry.BusinessObject}: {string.Join(", ", entry.Mapping.Select(m => m.Concept))}");
    return Results.Ok(entry);
});

// ── 제안 판단 (ARCHITECTURE §9 KPI '제안 수락률') ───────────────────────────
// 사용자가 제안을 수락했는지 거부했는지 보고한다. 무시는 보고하지 않는다 — 판단의 부재가 곧 무시다.
app.MapPost("/v1/suggestions/feedback",
    (HttpRequest request, SuggestionFeedbackRequest req, SuggestionFeedbackStore suggestions) =>
{
    if (RequestUser.From(request) is not { } userId)
        return Results.BadRequest(new { error = $"missing {RequestUser.Header} header" });

    var outcome = SuggestionOutcome.Normalize(req.Outcome);
    if (!SuggestionOutcome.IsDecision(outcome))
        return Results.BadRequest(new
        {
            error = $"outcome must be '{SuggestionOutcome.Accepted}' or '{SuggestionOutcome.Rejected}'",
            outcome = req.Outcome,
        });

    var decided = suggestions.Decide(
        userId, req.ObservationId, req.SuggestionId, outcome, DateTimeOffset.UtcNow);

    return decided is null
        ? Results.NotFound(new { error = "이 사용자에게 보여준 적 없는 제안이다" })
        : Results.Ok(decided);
});

// 수락률 집계 + 노출·판단 원자료. 운영은 접근통제(IAM) 하에 노출.
app.MapGet("/v1/suggestions", (SuggestionFeedbackStore suggestions, int? limit) =>
    Results.Ok(new { stats = suggestions.Stats(), records = suggestions.Snapshot(limit ?? 100) }));

// 검수·보정 결정 이력(읽기 전용). 운영은 접근통제(IAM) 하에 노출.
app.MapGet("/v1/decisions", (DecisionLog decisions, int? limit) =>
    Results.Ok(new { count = decisions.Count, entries = decisions.Snapshot(limit ?? 100) }));

app.Run();

// 통합 테스트에서 WebApplicationFactory로 참조하기 위한 partial 선언.
public partial class Program { }
