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

if (cfg.GetValue<bool>("UseBedrock"))
{
    var region = RegionEndpoint.GetBySystemName(cfg["Aws:Region"] ?? "ap-northeast-2");
    builder.Services.AddSingleton<IAmazonBedrockRuntime>(_ => new AmazonBedrockRuntimeClient(region));
    builder.Services.AddSingleton<IAiMapper>(sp => new BedrockAiMapper(
        sp.GetRequiredService<IAmazonBedrockRuntime>(),
        cfg["Aws:BedrockModelId"] ?? "anthropic.claude-3-5-sonnet-20240620-v1:0"));
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
    async (ObservationEvent evt, MappingResolver resolver, ObservationStore store) =>
{
    var signature = SignatureService.Compute(evt.Screen, evt.Tree);
    var hint = BusinessHint.FromScreen(evt.Screen);

    var res = await resolver.ResolveAsync(
        signature, evt.UserId, evt.Screen, evt.Tree, ProcurementOntology.Concepts, hint);
    var bo = BusinessObjectBuilder.Build(res.Entry, evt.Tree);

    store.Put(evt.EventId, evt, res.Entry, bo);

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

app.Run();
