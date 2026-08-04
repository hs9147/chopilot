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

builder.Services.AddSingleton(sp => new MappingResolver(
    sp.GetRequiredService<IMappingCache>(),
    sp.GetRequiredService<IAiMapper>(),
    cfg["Mapping:OrgId"] ?? "default",
    cfg.GetValue<double?>("Mapping:ThetaHigh") ?? 0.8));

var app = builder.Build();

// 측정 대시보드(wwwroot/index.html). PHASE0-MEASUREMENT의 jq/curl 절차를 화면으로 대체한다.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/observations",
    async (ObservationEvent evt, MappingResolver resolver, ObservationStore store,
           AuditService audit, UepStore uep) =>
{
    // 서명→매핑→BO 구간을 계측한다. 캐시 미스(=Bedrock 호출)와 HIT의 차이가 여기서 드러난다.
    var started = System.Diagnostics.Stopwatch.GetTimestamp();

    var signature = SignatureService.Compute(evt.Screen, evt.Tree);
    var hint = BusinessHint.FromScreen(evt.Screen);

    var res = await resolver.ResolveAsync(
        signature, evt.UserId, evt.Screen, evt.Tree, ProcurementOntology.Concepts, hint);
    var bo = BusinessObjectBuilder.Build(res.Entry, evt.Tree);

    var durationMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    store.Put(evt.EventId, evt, res.Entry, bo);
    audit.Record(evt, signature, res, durationMs);   // 불변 감사(Exit #4) + 지표 원자료

    // 개인화 축적(Exit ⑤, D5). 시각은 서버 수신 시각 — 클라이언트 시계에 좌우되면 안 된다.
    // route·title은 UEP를 사람이 읽을 수 있게 만든다: 해시만 남으면 다음작업 제안을 문장으로 쓸 수 없다.
    uep.RecordVisit(evt.UserId, signature, DateTimeOffset.UtcNow,
        SignatureService.NormalizeRoute(evt.Screen.Url), evt.Screen.Title);

    return Results.Ok(new
    {
        observation_id = evt.EventId,
        signature,
        status = "accepted",
        cache_hit = res.CacheHit,
        business_object = res.Entry.BusinessObject,
        confidence = res.Entry.Confidence
    });
});

app.MapGet("/v1/guide",
    (string observation_id, ObservationStore store, SuggestionFeedbackStore suggestions, UepStore uep) =>
{
    var rec = store.Get(observation_id);
    if (rec is null) return Results.NotFound(new { error = "unknown observation_id" });

    var guide = GuideService.Build(rec.Entry, rec.Event.Tree, rec.BusinessObject);

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
