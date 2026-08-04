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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/observations",
    async (ObservationEvent evt, MappingResolver resolver, ObservationStore store, AuditService audit) =>
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

app.MapGet("/v1/guide", (string observation_id, ObservationStore store) =>
{
    var rec = store.Get(observation_id);
    return rec is null
        ? Results.NotFound(new { error = "unknown observation_id" })
        : Results.Ok(GuideService.Build(rec.Entry, rec.Event.Tree, rec.BusinessObject));
});

// 감사 로그 조회(읽기 전용). 운영은 접근통제(IAM) 하에 노출.
app.MapGet("/v1/audit", (AuditService audit, int? limit) =>
    Results.Ok(new { count = audit.Count, entries = audit.Snapshot(limit ?? 100) }));

// H3b(캐시 적중률)·H6(지연 p95·AI 토큰) 자동 산출 — PHASE0-KIT 측정표의 원자료.
app.MapGet("/v1/metrics", (AuditService audit) => Results.Ok(audit.Metrics()));

app.Run();

// 통합 테스트에서 WebApplicationFactory로 참조하기 위한 partial 선언.
public partial class Program { }
