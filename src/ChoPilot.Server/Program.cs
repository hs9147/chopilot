using Amazon;
using Amazon.BedrockRuntime;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ChoPilot.Core;
using ChoPilot.Mapping;
using ChoPilot.Server;

// ─────────────────────────────────────────────────────────────────────────────
// ChoPilot.Server — Phase 1 Ingestion + Guide (읽기 전용)
//   POST /v1/observations : 관측 이벤트 → 서명 → 매핑 → Business Object 저장
//   GET  /v1/guide        : 현재 업무 요약 + 진행률 + 다음작업 힌트(Actionable=false)
//
// AI 매퍼는 설정으로 선택: 기본 Stub, Bedrock/Vertex AI(A DC)/Azure OpenAI endpoint.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 5 * 1024 * 1024);

// 클라이언트 JSON(PascalCase)과 record 생성자 파라미터 매칭
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("ingestion", limiter =>
    {
        limiter.PermitLimit = cfg.GetValue<int?>("Limits:IngestionPerMinute") ?? 120;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

// 영속화 (ARCHITECTURE §11). Storage:Path를 주지 않으면 지금까지와 똑같이 인메모리로 돈다 —
// 테스트와 CI가 디스크를 건드리지 않는 이유이자, 영속화가 선택인 이유다.
// 저장하지 않는 것: FoundationStore(외부 출처에서 다시 받는 게 맞다. 관측 파생물을
// 마스터로 굳히면 계보가 뒤집힌다 — §5.6).
builder.Services.AddSingleton<IJournalFactory>(_ =>
    cfg["Storage:Path"] is { Length: > 0 } path
        ? new FileJournalFactory(path)
        : NullJournalFactory.Instance);

builder.Services.AddSingleton<IMappingCache>(sp =>
    new InMemoryMappingCache(sp.GetRequiredService<IJournalFactory>()));
builder.Services.AddSingleton<SingleFlight>();
builder.Services.AddSingleton<ObservationStore>();
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton(sp => new UepStore(sp.GetRequiredService<IJournalFactory>()));
builder.Services.AddSingleton<DecisionLog>();
builder.Services.AddSingleton<SuggestionFeedbackStore>();
builder.Services.AddSingleton<PersonalizationService>();
builder.Services.AddSingleton<IngestionCoordinator>();
builder.Services.AddSingleton<FeedbackService>();

// Curated Knowledge Plane (ARCHITECTURE §5.4 Plane 3) — 온톨로지·규칙의 런타임 진실.
builder.Services.AddSingleton<KnowledgeStore>();
builder.Services.AddSingleton<UnknownConceptLog>();
builder.Services.AddSingleton<EntityStore>();
builder.Services.AddSingleton<KnowledgeViewRenderer>();
builder.Services.AddSingleton<CompletionStore>();

// ── 기반 정보 축 출처 (무료 API · MCP) ──────────────────────────────────────
// 등록 순서가 병합 우선순위다: 뒤에 등록된 출처가 앞을 덮는다.
// 내장 표준 → 키 없는 무료 API → 공공데이터포털(키 필요) → 조직이 지정한 MCP 서버.
// 네트워크 출처는 <b>기본 전부 비활성</b>이다 — 켜지 않으면 테스트도 CI도 밖으로 나가지 않는다.
builder.Services.AddHttpClient();
var llmTimeoutSeconds = Math.Clamp(cfg.GetValue<int?>("Llm:TimeoutSeconds") ?? 45, 5, 300);
builder.Services.AddHttpClient("llm", client => client.Timeout = TimeSpan.FromSeconds(llmTimeoutSeconds));
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
    sp.GetRequiredService<CompletionStore>(),
    cfg.GetValue<int?>("Knowledge:MinSupport") ?? AxisAggregator.DefaultMinSupport,
    cfg.GetValue<int?>("Knowledge:MinDistinctUsers") ?? AxisAggregator.DefaultMinDistinctUsers));
builder.Services.AddSingleton<IKnowledgeProvider>(sp => sp.GetRequiredService<KnowledgeStore>());
builder.Services.AddSingleton(sp => new KnowledgeService(
    sp.GetRequiredService<KnowledgeStore>(),
    sp.GetRequiredService<IMappingCache>(),
    cfg.GetValue<double?>("Mapping:ThetaHigh") ?? 0.8));

// 요청 주체 해석 (ARCHITECTURE §8). 검증하지 않는 헤더 방식은 운영 환경에서 기동을 거부한다 —
// 경고 로그로 두면 아무도 읽지 않고, 그 서버는 헤더 한 줄로 누구든 사칭할 수 있는 채로 돈다.
builder.Services.AddSingleton(_ =>
    AuthenticationSetup.Create(cfg, builder.Environment.IsProduction()));

// 서버측 잔존 PII 스캔용(H4 반증). 마스킹 자체는 클라이언트가 이미 수행했다.
builder.Services.AddSingleton(_ => new PrivacyGate(policyVersion: cfg["Privacy:PolicyVersion"] ?? "1.0"));

var configuredLlmProvider = cfg["Llm:Provider"]?.Trim().ToLowerInvariant();
var llmProvider = string.IsNullOrWhiteSpace(configuredLlmProvider)
    ? (cfg.GetValue<bool>("UseBedrock") ? "bedrock" : "stub") // 기존 설정 호환
    : configuredLlmProvider;

switch (llmProvider)
{
    case "stub":
        builder.Services.AddSingleton<IAiMapper, StubAiMapper>();
        break;

    case "bedrock":
    {
        var region = RegionEndpoint.GetBySystemName(cfg["Aws:Region"] ?? "ap-northeast-2");
        builder.Services.AddSingleton<IAmazonBedrockRuntime>(_ => new AmazonBedrockRuntimeClient(region));
        // 현재 Anthropic 모델은 inference profile ID만 지원(ON_DEMAND base id → ResourceNotFoundException).
        builder.Services.AddSingleton<IAiMapper>(sp => new BedrockAiMapper(
            sp.GetRequiredService<IAmazonBedrockRuntime>(),
            cfg["Aws:BedrockModelId"] ?? "us.anthropic.claude-haiku-4-5-20251001-v1:0"));
        break;
    }

    case "vertex":
    case "vertex_ai":
    {
        var vertexOptions = new VertexAiOptions(
            cfg["Llm:Vertex:ProjectId"] ?? "",
            cfg["Llm:Vertex:Location"] ?? "",
            cfg["Llm:Vertex:Model"] ?? "");
        vertexOptions.Validate(); // 잘못된 설정으로 빈 서버가 뜨는 것을 막는다.
        builder.Services.AddSingleton<ILlmCompletionClient>(sp => new VertexAiCompletionClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm"),
            vertexOptions));
        builder.Services.AddSingleton<IAiMapper>(sp => new CompletionClientAiMapper(
            sp.GetRequiredService<ILlmCompletionClient>()));
        break;
    }

    case "azure":
    case "azure_openai":
    {
        var azureOptions = new AzureOpenAiOptions(
            cfg["Llm:AzureOpenAI:Endpoint"] ?? "",
            cfg["Llm:AzureOpenAI:Deployment"] ?? "",
            cfg["Llm:AzureOpenAI:ApiVersion"] ?? "",
            cfg["Llm:AzureOpenAI:ApiKey"],
            cfg["Llm:AzureOpenAI:BearerToken"]);
        azureOptions.Validate(); // credential 누락을 첫 관측까지 미루지 않는다.
        builder.Services.AddSingleton<ILlmCompletionClient>(sp => new AzureOpenAiCompletionClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm"),
            azureOptions));
        builder.Services.AddSingleton<IAiMapper>(sp => new CompletionClientAiMapper(
            sp.GetRequiredService<ILlmCompletionClient>()));
        break;
    }

    default:
        throw new InvalidOperationException(
            $"Llm:Provider '{llmProvider}'는 stub|bedrock|vertex|azure_openai 중 하나여야 한다");
}

// 지식 초안 서술(§5.5 3단계). 기본은 AI 없음 — 루프는 LLM 없이도 완결된다.
// Knowledge:UseEditor=true 일 때만 배치에서 초안 1건당 1회 호출된다.
if (llmProvider == "bedrock" && cfg.GetValue<bool>("Knowledge:UseEditor"))
{
    builder.Services.AddSingleton<IKnowledgeEditor>(sp => new BedrockKnowledgeEditor(
        sp.GetRequiredService<IAmazonBedrockRuntime>(),
        cfg["Knowledge:EditorModelId"] ?? cfg["Aws:BedrockModelId"]
            ?? "us.anthropic.claude-haiku-4-5-20251001-v1:0"));
}
else if (llmProvider is "vertex" or "vertex_ai" or "azure" or "azure_openai" &&
         cfg.GetValue<bool>("Knowledge:UseEditor"))
{
    builder.Services.AddSingleton<IKnowledgeEditor>(sp => new LlmKnowledgeEditor(
        sp.GetRequiredService<ILlmCompletionClient>()));
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

// 저장소를 미리 만든다 — 지연 생성이면 복원 실패가 첫 요청에서야 드러난다.
// 시작하자마자 무엇이 얼마나 복원됐는지(그리고 무엇이 손상됐는지) 로그에 남긴다.
{
    var journals = app.Services.GetRequiredService<IJournalFactory>();
    foreach (var eager in new object[]
             {
                 app.Services.GetRequiredService<IMappingCache>(),
                 app.Services.GetRequiredService<ObservationStore>(),
                 app.Services.GetRequiredService<AuditService>(),
                 app.Services.GetRequiredService<UepStore>(),
                 app.Services.GetRequiredService<DecisionLog>(),
                 app.Services.GetRequiredService<SuggestionFeedbackStore>(),
                 app.Services.GetRequiredService<KnowledgeStore>(),
                 app.Services.GetRequiredService<UnknownConceptLog>(),
                 app.Services.GetRequiredService<EntityStore>(),
                 app.Services.GetRequiredService<CompletionStore>(),
                 app.Services.GetRequiredService<FeedbackService>(),
             }) _ = eager;

    var auth = app.Services.GetRequiredService<IUserPrincipalResolver>();
    var authLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Auth");
    if (auth.Verifies)
        authLog.LogInformation("주체 해석: {Method} (검증됨)", auth.Method);
    else
        authLog.LogWarning("주체 해석: {Method} — 검증되지 않는다. " +
            "{Header} 헤더를 대는 누구든 다른 사용자로 행세할 수 있으므로 " +
            "신뢰 경계 밖(VPC/mTLS 밖)에 노출하면 안 된다.", auth.Method, RequestUser.Header);

    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Storage");
    if (journals is FileJournalFactory files)
    {
        var status = files.Status();
        log.LogInformation("저널 복원: {Dir} — {Records}건, 손상 {Corrupt}줄",
            files.Directory, status.Sum(s => s.Restored), status.Sum(s => s.Corrupt));
        foreach (var s in status.Where(s => s.Corrupt > 0))
            log.LogWarning("저널 '{Name}'에서 {Corrupt}줄을 건너뛰었다 (쓰기 도중 종료된 흔적)", s.Name, s.Corrupt);
    }
    else
    {
        log.LogWarning("영속화 없음 — 재시작하면 지식·매핑 캐시·감사 로그가 사라진다. Storage:Path로 켠다.");
    }
}

// 주체 관문은 라우팅보다 앞이다 — 엔드포인트마다 인증을 반복하면 그중 하나는 반드시 빠진다.
app.UseMiddleware<PrincipalMiddleware>();
app.UseMiddleware<ApiAuthorizationMiddleware>();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("Referrer-Policy", "same-origin");
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'self'; frame-ancestors 'none'");
    await next();
});

// 측정 대시보드(wwwroot/index.html). PHASE0-MEASUREMENT의 jq/curl 절차를 화면으로 대체한다.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// 인증 상태 (ARCHITECTURE §8). verified=false면 헤더를 대는 누구든 사칭할 수 있다는 뜻이다.
app.MapGet("/v1/auth", (IUserPrincipalResolver resolver, HttpRequest request) => Results.Ok(new
{
    method = resolver.Method,
    verified = resolver.Verifies,
    principal = RequestUser.Principal(request)?.UserId,
}));

// 영속화 상태 (ARCHITECTURE §11). durable=false면 재시작에 전부 사라진다는 뜻이다.
app.MapGet("/v1/storage", (IJournalFactory journals) =>
{
    var files = journals as FileJournalFactory;
    var status = files?.Status() ?? Array.Empty<JournalStatus>();

    return Results.Ok(new
    {
        durable = files is not null,
        path = files?.Directory,
        restored = status.Sum(s => s.Restored),
        corrupt = status.Sum(s => s.Corrupt),
        journals = status,
    });
});

app.MapPost("/v1/observations",
    async (HttpRequest request, ObservationEvent? evt, MappingResolver resolver, ObservationStore store,
           AuditService audit, UepStore uep, IKnowledgeProvider knowledgeProvider,
           EntityStore entities, CompletionStore completions, IngestionCoordinator coordinator,
           PrivacyGate privacyGate, CancellationToken ct) =>
{
    // 계약 위반은 장애가 아니라 거부다. 500을 내면 클라이언트 스풀이 그것을 서버 장애로 읽고
    // 순서 보존을 위해 큐 머리에 남긴다 — 그 뒤의 모든 관측이 영원히 도달하지 못한다.
    if (!ObservationContract.IsValid(evt, out var violation))
        return Results.BadRequest(new { error = "invalid observation event", detail = violation });

    var authResolver = request.HttpContext.RequestServices.GetRequiredService<IUserPrincipalResolver>();
    if (authResolver.Verifies && privacyGate.ScanResidual(evt.Screen, evt.Tree).Count > 0)
        return Results.BadRequest(new { error = "privacy_residual_detected" });
    var principal = RequestUser.Principal(request);
    if (principal is null && !authResolver.Verifies)
        principal = new UserPrincipal(evt.UserId, AuthMethod.TrustedHeader, "default", ChoPilotRole.All);
    if (principal is null || !(principal.IsInRole(ChoPilotRole.IngestionClient) ||
                               principal.IsInRole(ChoPilotRole.EndUser)))
        return RequestUser.RequireAnyRole(request, out _, ChoPilotRole.IngestionClient, ChoPilotRole.EndUser)!;

    // 본문은 신원을 정하지 못한다. 전환 기간에는 필드를 유지하되 검증된 주체와 다르면 즉시 거부한다.
    if (!string.Equals(evt.UserId, principal.UserId, StringComparison.Ordinal))
        return Results.Json(new
        {
            error = "principal_mismatch",
            detail = "observation.userId must match the authenticated subject",
        }, statusCode: StatusCodes.Status403Forbidden);

    evt = evt with { UserId = principal.UserId };

    await using var lease = await coordinator.AcquireAsync(
        principal.TenantId, principal.UserId, evt.EventId, ct);

    // at-least-once 재전송은 최초 영수증을 그대로 돌려주며 AI·Audit·UEP·Entity를 다시 실행하지 않는다.
    if (store.Get(evt.EventId, principal.TenantId) is { } accepted)
        return ObservationAccepted(accepted, replayed: true);

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

    audit.Record(evt, signature, res, durationMs, principal.TenantId);   // 불변 감사(Exit #4) + 지표 원자료

    // 개인화 축적(Exit ⑤, D5). 시각은 서버 수신 시각 — 클라이언트 시계에 좌우되면 안 된다.
    // route·title은 UEP를 사람이 읽을 수 있게 만든다: 해시만 남으면 다음작업 제안을 문장으로 쓸 수 없다.
    uep.RecordVisit(evt.UserId, signature, DateTimeOffset.UtcNow,
        SignatureService.NormalizeRoute(evt.Screen.Url), evt.Screen.Title,
        evt.EventId, principal.TenantId);

    // 엔티티 결정 1단(§6 Deterministic). BO의 비민감 값만 보므로 단가·금액은 애초에 도달하지 않는다.
    entities.Record(EntityResolver.Extract(bo, knowledge), evt.UserId, signature,
        DateTimeOffset.UtcNow, evt.EventId, principal.TenantId);

    // 작업 완료 신호(§11). 저장을 누른 순간의 화면만이 "이 업무객체가 실제로 무엇을 요구했는가"를
    // 말해 준다 — 작성 중간의 빈칸은 아직 안 채운 것인지 필요 없는 것인지 구분되지 않는다.
    // 개념 이름과 개수만 남고 값은 남지 않는다.
    if (ObservationTrigger.IsCompletion(evt.Trigger))
        completions.Record(new CompletionRecord(
            res.Entry.BusinessObject, evt.UserId, signature,
            FieldFill.Observed(res.Entry),
            FieldFill.Filled(res.Entry, evt.Tree).ToList(),
            DateTimeOffset.UtcNow,
            evt.EventId,
            principal.TenantId));

    // 완료 표식은 마지막에 기록한다. 앞 단계가 실패하면 재시도가 누락 projection만 보충한다.
    store.Put(evt.EventId, evt, res.Entry, bo, principal.TenantId, res.CacheHit, res.Source);
    return ObservationAccepted(store.Get(evt.EventId, principal.TenantId)!, replayed: false);
}).RequireRateLimiting("ingestion");

app.MapGet("/v1/guide",
    (string observation_id, HttpRequest request, ObservationStore store, UepStore uep,
     SuggestionFeedbackStore suggestions,
     IKnowledgeProvider knowledgeProvider) =>
{
    var authResolver = request.HttpContext.RequestServices.GetRequiredService<IUserPrincipalResolver>();
    var principal = RequestUser.Principal(request);
    if (principal is null && !authResolver.Verifies)
    {
        // 로컬 측정 모드는 body user가 유일한 식별자다. JWT 운영 모드에서는 이 fallback이 없다.
        var local = store.Get(observation_id);
        if (local is null) return Results.NotFound(new { error = "unknown observation_id" });
        principal = new UserPrincipal(local.Event.UserId, AuthMethod.TrustedHeader, local.TenantId, ChoPilotRole.All);
    }
    if (principal is null || !(principal.IsInRole(ChoPilotRole.EndUser) ||
                               principal.IsInRole(ChoPilotRole.OpsAuditor)))
        return RequestUser.RequireAnyRole(request, out _, ChoPilotRole.EndUser, ChoPilotRole.OpsAuditor)!;

    var rec = store.Get(observation_id, principal.TenantId);
    if (rec is null) return Results.NotFound(new { error = "unknown observation_id" });
    if (rec.Event.UserId != principal.UserId && !principal.IsInRole(ChoPilotRole.OpsAuditor))
        return Results.NotFound(new { error = "unknown observation_id" });

    var guide = GuideService.Build(rec.Entry, rec.Event.Tree, rec.BusinessObject, knowledgeProvider.Current);

    // 화면 안의 빈칸 힌트 뒤에 "이 화면 다음에 무엇을 하는가"를 붙인다.
    // 화면 하나만 보면 다음 '작업'이 아니라 다음 '입력칸'까지만 말할 수 있다(UEP 전이, D5).
    var next = uep.NextScreens(rec.Event.UserId, rec.Entry.Signature,
            limit: 2, tenantId: principal.TenantId)
        .Select(t => GuideService.NextScreenHint(guide.BusinessObject, t));
    guide = guide with { NextHints = guide.NextHints.Concat(next).ToList() };

    // 호환성: 개발 전용 trusted-header 측정 콘솔은 기존 GET 기반 노출 계측을 유지한다.
    // JWT/OIDC 운영 경로에서는 GET이 상태를 바꾸지 않으며 /v1/suggestions/impressions만 쓴다.
    if (!authResolver.Verifies)
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
// 겹쳐 들어오면 외부 API를 두 번 때린다 — 무료 출처는 대개 일일 할당량이 있다. 두 번째는 거절한다.
app.MapPost("/v1/foundation/refresh",
    async (HttpRequest request, FoundationStore foundation, EntityStore entities,
           DecisionLog decisions, SingleFlight inFlight, CancellationToken ct) =>
{
    if (RequestUser.Require(request, out var actor) is { } unauthenticated)
        return unauthenticated;

    var payload = await inFlight.RunAsync("foundation_refresh", async () =>
    {
        var results = await foundation.RefreshAsync(FoundationStore.QueryFrom(entities), ct);
        var failed = results.Where(r => !r.Ok).ToList();

        decisions.Record("foundation_refresh", actor, "foundation", "global", 1.0,
            $"{results.Count}개 출처, 사실 {foundation.Master.Count}건, 실패 {failed.Count}건");

        return new
        {
            refreshed = results.Count,
            facts = foundation.Master.Count,
            failures = failed.Select(f => new { source = f.SourceId, error = f.Error }),
            sources = foundation.Status(),
        };
    });

    return payload is null
        ? Results.Conflict(new { error = "출처 갱신이 이미 실행 중이다 — 끝나면 다시 눌러라." })
        : Results.Ok(payload);
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
    if (RequestUser.Require(request, out var userId) is { } unauthenticated)
        return unauthenticated;

    var profile = uep.Get(userId);
    return profile is null
        ? Results.NotFound(new { error = "no profile for user" })
        : Results.Ok(profile);
});

// 보정 폼이 쓸 개념 목록. 사용자는 "UnitPrice"가 아니라 "단가"로 정정하므로 별칭까지 내려준다.
// 하드코딩이 아니라 게시된 지식의 컴파일 결과다 — 개념 문서가 승인되면 여기 즉시 나타난다.
app.MapGet("/v1/ontology", (IKnowledgeProvider knowledge) =>
    Results.Ok(new { version = knowledge.Current.Version, concepts = knowledge.Current.Concepts }));

// ── 사용자 검측·피드백 (D7, ARCHITECTURE §3.5/§4.4) ────────────────────────
app.MapGet("/v1/me/review-tasks",
    (HttpRequest request, FeedbackService feedback, int? limit) =>
{
    if (RequestUser.RequireAnyRole(request, out var principal, ChoPilotRole.EndUser) is { } unauthorized)
        return unauthorized;
    return Results.Ok(new
    {
        tasks = feedback.Tasks(principal.TenantId, principal.UserId, limit ?? 50),
    });
});

app.MapPost("/v1/feedback",
    (HttpRequest request, FeedbackCommand command, FeedbackService feedback) =>
{
    if (RequestUser.RequireAnyRole(request, out var principal, ChoPilotRole.EndUser) is { } unauthorized)
        return unauthorized;

    var result = feedback.Submit(principal.TenantId, principal.UserId, command);
    if (result.Conflict)
        return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict);
    return result.Record is null
        ? Results.BadRequest(new { error = result.Error })
        : Results.Ok(result.Record);
});

app.MapPost("/v1/feedback/{id}/undo",
    (string id, HttpRequest request, FeedbackService feedback) =>
{
    if (RequestUser.RequireAnyRole(request, out var principal, ChoPilotRole.EndUser) is { } unauthorized)
        return unauthorized;
    var result = feedback.Undo(principal.TenantId, principal.UserId, id);
    return result.Record is null
        ? Results.BadRequest(new { error = result.Error })
        : Results.Ok(result.Record);
});

app.MapGet("/v1/reviews/feedback",
    (HttpRequest request, FeedbackService feedback, int? limit) =>
{
    if (RequestUser.RequireAnyRole(request, out var principal, ChoPilotRole.Reviewer) is { } unauthorized)
        return unauthorized;
    return Results.Ok(new { entries = feedback.PendingReviews(principal.TenantId, limit ?? 100) });
});

app.MapPost("/v1/reviews/{id}/decision",
    (string id, HttpRequest request, FeedbackReviewDecision decision, FeedbackService feedback) =>
{
    if (RequestUser.RequireAnyRole(request, out var principal, ChoPilotRole.Reviewer) is { } unauthorized)
        return unauthorized;
    var result = feedback.Review(principal.TenantId, principal.UserId, id, decision.Approve);
    return result.Record is null
        ? Results.NotFound(new { error = result.Error })
        : Results.Ok(result.Record);
});

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
    if (RequestUser.Require(request, out var author) is { } unauthenticated)
        return unauthenticated;

    var (draft, error) = svc.Submit(doc, author);
    return draft is null ? Results.BadRequest(new { error }) : Results.Ok(draft);
});

// 축별 집계 → 초안 생성 (ARCHITECTURE §5.5 1~2단계). 운영은 일 배치, 여기선 수동 트리거.
// LLM은 여기 없다 — 결정적 집계만으로 초안이 만들어지고, AI는 본문 품질 개선(3단계)에만 쓰인다.
app.MapPost("/v1/knowledge/aggregate",
    async (HttpRequest request, AxisAggregator aggregator, KnowledgeService svc,
           IKnowledgeEditor editor, SingleFlight inFlight, bool? dryRun, CancellationToken ct) =>
{
    if (RequestUser.Require(request, out var actor) is { } unauthenticated)
        return unauthenticated;

    // 미리보기는 게이트 밖이다 — 쓰지도, 밖으로 호출하지도 않는다. 제출이 도는 동안에도 봐야 한다.
    if (dryRun == true)
    {
        var preview = aggregator.Aggregate(DateTimeOffset.UtcNow);
        return Results.Ok(new { dryRun = true, drafts = preview.Drafts, skipped = preview.Skipped });
    }

    // 집계는 "이미 존재"를 Aggregate() 안에서 판정하는데, 그 판정은 Submit보다 앞선다.
    // 그래서 겹쳐 들어오면 둘 다 같은 초안 목록을 만들고 초안 1건당 LLM을 두 번 부른 뒤에야
    // 두 번째가 거부된다 — 중복 판정이 비용보다 뒤에 있다. 진입에서 끊는다.
    var payload = await inFlight.RunAsync("knowledge_aggregate", async () =>
    {
        var result = aggregator.Aggregate(DateTimeOffset.UtcNow);

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

        return new
        {
            submitted = submitted.Count,
            drafts = submitted,
            skipped = result.Skipped.Concat(rejected).ToList(),
        };
    });

    return payload is null
        ? Results.Conflict(new { error = "집계가 이미 실행 중이다 — 끝나면 다시 눌러라." })
        : Results.Ok(payload);
});

// 작업 완료 신호 집계 (ARCHITECTURE §11). 필수 필드 규칙 개정의 근거다.
// fillRate는 "화면에 있었을 때 채워져 있던 비율" — 화면에 없던 개념은 분모에도 들어가지 않는다.
app.MapGet("/v1/completions", (CompletionStore completions, int? limit) =>
    Results.Ok(new
    {
        count = completions.Count,
        businessObjects = completions.Stats(),
        recent = completions.Snapshot(limit ?? 50),
    }));

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
    if (RequestUser.Require(request, out var approver) is { } unauthenticated)
        return unauthenticated;

    var (doc, error) = svc.Approve(id, approver);
    if (doc is null) return Results.BadRequest(new { error });

    decisions.Record("knowledge_publish", approver, doc.Id, doc.Scope, 1.0,
        $"{doc.Type} v{doc.Version}: {doc.Title}");
    return Results.Ok(doc);
});

app.MapPost("/v1/knowledge/{id}/deprecate",
    (string id, HttpRequest request, KnowledgeService svc, DecisionLog decisions) =>
{
    if (RequestUser.Require(request, out var actor) is { } unauthenticated)
        return unauthenticated;

    var outcome = svc.Deprecate(id, actor);
    if (outcome.Doc is null) return Results.BadRequest(new { error = outcome.Error });

    decisions.Record("knowledge_deprecate", actor, outcome.Doc.Id, outcome.Doc.Scope, 0,
        $"{outcome.Doc.Type} v{outcome.Doc.Version}: {outcome.Doc.Title} (매핑 {outcome.TouchedMappings}건 정리)");
    return Results.Ok(new { doc = outcome.Doc, touchedMappings = outcome.TouchedMappings });
});

// 검수 큐(HITL) — 저신뢰 매핑 열람. 개인 스코프는 제외된다.
// 매핑은 ref와 정규 개념만 들고 있다(n2 → Vendor). 사람이 그 판단을 검수하려면 n2가 화면의
// 어느 칸이었는지 알아야 하는데 그건 화면 쪽에만 있다 — 서명으로 이어 붙여 함께 내려보낸다.
// thetaHigh도 같이 준다: 신뢰도 0.60이 통과인지 아닌지는 임계치를 알아야만 말할 수 있다.
app.MapGet("/v1/review", (PersonalizationService svc, ObservationStore store, IConfiguration config, int? limit) =>
    Results.Ok(new
    {
        entries = svc.ReviewQueue(limit ?? 100),
        screens = MeasurementViews.ScreensBySignature(store.List()),
        thetaHigh = config.GetValue("Mapping:ThetaHigh", 0.8),
    }));

// AI 추정 이력 — 검수 큐가 "손봐야 하는 것"이라면 이쪽은 trusted까지 포함한 대장이다.
// 승격·보정으로 큐에서 빠진 판단도 계속 쓰이므로, 그것까지 보여야 서 있는 판단 전부가 보인다.
app.MapGet("/v1/inferences",
    (HttpRequest request, PersonalizationService svc, ObservationStore store,
     IConfiguration config, int? limit) =>
{
    if (RequestUser.Require(request, out var actor) is { } unauthenticated)
        return unauthenticated;

    var ledger = svc.Inferences(actor, limit ?? 200);
    return Results.Ok(new
    {
        entries = ledger.Entries,
        total = ledger.Total,          // 자르기 전 개수 — 몇 건이 잘렸는지 화면이 말할 수 있어야 한다
        screens = MeasurementViews.ScreensBySignature(store.List()),
        thetaHigh = config.GetValue("Mapping:ThetaHigh", 0.8),
    });
});

// 추정 제외 — 되돌리는 게 아니라 다시 묻게 하는 것이다. 엔트리가 사라지면 재추론 백오프의
// 기준도 사라져 다음 관측에서 곧바로 AI를 다시 부른다. 공용 평면을 지우면 모두에게 영향이
// 가므로 누가 지웠는지 결정 이력에 남긴다.
app.MapPost("/v1/inference/discard",
    (HttpRequest request, PromoteRequest req, PersonalizationService svc, DecisionLog decisions) =>
{
    if (RequestUser.Require(request, out var actor) is { } unauthenticated)
        return unauthenticated;

    var discarded = svc.Discard(req.Signature, req.Scope, actor);
    if (discarded is null)
        return Results.NotFound(new { error = "그 스코프에 지울 추정이 없다" });   // 남의 개인 매핑도 여기로 떨어진다

    decisions.Record("inference_discard", actor, discarded.Signature, discarded.Scope,
        discarded.Confidence,
        $"{discarded.BusinessObject} 필드 {discarded.Mapping.Count}개 제외 — 다음 관측에서 재추론된다");

    return Results.Ok(new { discarded = true, discarded.Signature, discarded.Scope });
});

// 검수 통과분을 trusted로 승격(HITL). 누가 승인했는지 결정 이력에 남긴다.
app.MapPost("/v1/review/promote",
    (HttpRequest request, PromoteRequest req, PersonalizationService svc, DecisionLog decisions) =>
{
    if (RequestUser.Require(request, out var actor) is { } unauthenticated)
        return unauthenticated;

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
    if (RequestUser.Require(request, out var userId) is { } unauthenticated)
        return unauthenticated;

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
app.MapPost("/v1/suggestions/impressions",
    (HttpRequest request, SuggestionImpressionRequest req, ObservationStore store,
     SuggestionFeedbackStore suggestions, UepStore uep, IKnowledgeProvider knowledgeProvider) =>
{
    if (RequestUser.RequireAnyRole(request, out var principal, ChoPilotRole.EndUser) is { } unauthorized)
        return unauthorized;

    var rec = store.Get(req.ObservationId, principal.TenantId);
    if (rec is null || rec.Event.UserId != principal.UserId)
        return Results.NotFound(new { error = "unknown observation_id" });

    var guide = GuideService.Build(rec.Entry, rec.Event.Tree, rec.BusinessObject, knowledgeProvider.Current);
    var next = uep.NextScreens(rec.Event.UserId, rec.Entry.Signature,
            limit: 2, tenantId: principal.TenantId)
        .Select(t => GuideService.NextScreenHint(guide.BusinessObject, t));
    guide = guide with { NextHints = guide.NextHints.Concat(next).ToList() };

    var added = suggestions.RecordImpressions(
        principal.UserId, req.ObservationId, rec.Entry.Signature,
        guide.BusinessObject, guide.NextHints, DateTimeOffset.UtcNow);
    return Results.Ok(new { observation_id = req.ObservationId, added });
});

app.MapPost("/v1/suggestions/feedback",
    (HttpRequest request, SuggestionFeedbackRequest req, SuggestionFeedbackStore suggestions) =>
{
    if (RequestUser.Require(request, out var userId) is { } unauthenticated)
        return unauthenticated;

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

static IResult ObservationAccepted(StoredObservation rec, bool replayed) => Results.Ok(new
{
    observation_id = rec.ObservationId,
    signature = rec.Entry.Signature,
    status = "accepted",
    replayed,
    cache_hit = rec.CacheHit,
    source = rec.Source,
    business_object = rec.Entry.BusinessObject,
    confidence = rec.Entry.Confidence,
});

app.Run();

// 통합 테스트에서 WebApplicationFactory로 참조하기 위한 partial 선언.
public partial class Program { }

public sealed record SuggestionImpressionRequest(string ObservationId);
public sealed record FeedbackReviewDecision(bool Approve);
