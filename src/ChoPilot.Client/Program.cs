using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using ChoPilot.Client;
using ChoPilot.Core;
using ChoPilot.Mapping;
using Microsoft.Extensions.Configuration;

// ─────────────────────────────────────────────────────────────────────────────
// chopilot-dump — Phase 0 UIA 관측 실측 도구 (PHASE0-PLAN WS2/WS3)
//
//   사용법:
//     chopilot-dump [--out <파일>] [--delay <초>] [--baseline] [--bedrock]
//                   [--upload [url]] [--spool-dir <경로>]
//
//   동작:
//     1. --delay 초 대기 (그 사이 관측할 브라우저 화면을 포그라운드로)
//     2. 포그라운드 창의 접근성 트리를 정규화 수집 + 화면/레코드 식별(ScreenIdentifier)
//     3. ConsentPolicy(on/off·앱별 제외) → PrivacyGate(마스킹) → 화면 서명 계산
//     4. --baseline : StubAiMapper(alias) 매핑 시도 (대조군)
//        --bedrock  : Bedrock 동적 매핑 시도 (실 AI, appsettings 설정 사용)
//        --upload [url] : 서버로 POST 후 Guide 조회 (기본 Server:IngestionEndpoint)
//                         전송 실패 시 durable 스풀에 적재, 다음 실행에서 재전송
//        --spool-dir    : 스풀 디렉터리 지정 (기본 <실행경로>/spool)
//     5. ObservationEvent JSON을 stdout + (옵션)파일로 출력
//
//   설정: appsettings.json → appsettings.local.json → 환경변수(CHOPILOT_*) 순 오버라이드
// ─────────────────────────────────────────────────────────────────────────────

var cfg = LoadConfig();
var opts = ParseArgs(args);

if (opts.Delay > 0)
{
    Console.Error.WriteLine($"[chopilot-dump] {opts.Delay}s 후 포그라운드 창을 캡처합니다. 대상 화면을 앞으로 두세요...");
    await Task.Delay(TimeSpan.FromSeconds(opts.Delay));
}

using var observer = new UiaObserver();
var (screen, rawTree) = observer.CaptureForegroundWindow(cfg.Observation.MaxDepth, cfg.Observation.MaxNodes);

// 관측 동의·범위 게이트 (전역 on/off + 앱별 제외). 미허용 시 관측·전송하지 않는다.
var consent = new ConsentPolicy(cfg.Consent).Evaluate(screen);
if (!consent.Allowed)
{
    Console.Error.WriteLine($"[chopilot-dump] 관측 중단 — {consent.Reason}");
    return 0;
}

var gate = new PrivacyGate(policyVersion: cfg.Privacy.PolicyVersion);
var (maskedTree, maskedRefs) = gate.Apply(rawTree);

var signature = SignatureService.Compute(screen, maskedTree);

var evt = new ObservationEvent(
    EventId: Guid.NewGuid().ToString(),
    SessionId: Environment.MachineName,
    UserId: HashUser(Environment.UserName),
    CapturedAt: DateTimeOffset.Now,
    Screen: screen,
    Tree: maskedTree,
    Privacy: new PrivacyInfo(gate.PolicyVersion, maskedRefs));

var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

Console.Error.WriteLine($"[chopilot-dump] signature = {signature}");
Console.Error.WriteLine($"[chopilot-dump] masked refs = {maskedRefs.Count}");

if (opts.Upload)
{
    var url = opts.UploadUrl
        ?? (string.IsNullOrWhiteSpace(cfg.Server.IngestionEndpoint) ? "http://127.0.0.1:5080" : cfg.Server.IngestionEndpoint);
    var spoolDir = opts.SpoolDir ?? Path.Combine(AppContext.BaseDirectory, "spool");
    var spool = new EventSpool(spoolDir);
    Console.Error.WriteLine($"[chopilot-dump] upload → {url} (spool: {spoolDir}, 대기 {spool.PendingCount})");
    using var uploader = new Uploader(url);

    // 밀린 스풀 재전송 → 현재 이벤트 전송. 실패분은 유실 대신 스풀에 남는다.
    var dispatch = await ObservationDispatcher.DispatchAsync(
        spool, evt, e => uploader.TryPostObservationAsync(e));

    if (dispatch.Drained > 0) Console.Error.WriteLine($"[chopilot-dump] spool 재전송 {dispatch.Drained}건");

    if (dispatch.Sent)
        Console.Error.WriteLine("[chopilot-dump] guide : " + await uploader.GetGuideAsync(evt.EventId));
    else
        Console.Error.WriteLine($"[chopilot-dump] 전송 실패 → 스풀 적재(대기 {dispatch.Pending})");
}

if (opts.Baseline)
    await RunMapper("baseline(stub)", new StubAiMapper(), maskedTree);

if (opts.Bedrock)
{
    Console.Error.WriteLine($"[chopilot-dump] Bedrock: region={cfg.Aws.Region}, model={cfg.Aws.BedrockModelId}");
    var region = RegionEndpoint.GetBySystemName(cfg.Aws.Region);
    using var bedrock = new AmazonBedrockRuntimeClient(region);
    await RunMapper("bedrock(ai)", new BedrockAiMapper(bedrock, cfg.Aws.BedrockModelId), maskedTree);
}

var output = JsonSerializer.Serialize(new { signature, observation = evt }, jsonOpts);
Console.WriteLine(output);

if (opts.OutFile is not null)
{
    var dir = Path.GetDirectoryName(opts.OutFile);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(opts.OutFile, output);
    Console.Error.WriteLine($"[chopilot-dump] wrote {opts.OutFile}");
}

return 0;

static async Task RunMapper(string label, IAiMapper mapper, UiNode tree)
{
    try
    {
        var inf = await mapper.InferAsync("PurchaseRequest", tree, ProcurementOntology.Concepts);
        Console.Error.WriteLine($"[chopilot-dump] {label} mapped {inf.Fields.Count} fields:");
        foreach (var f in inf.Fields)
            Console.Error.WriteLine($"    {f.ElementRef} -> {f.Concept} (conf {f.Confidence:0.00}, {f.Provenance})");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[chopilot-dump] {label} 실패: {ex.Message}");
    }
}

static ChoPilotConfig LoadConfig()
{
    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.local.json", optional: true)
        .AddEnvironmentVariables("CHOPILOT_")
        .Build();
    return config.Get<ChoPilotConfig>() ?? new ChoPilotConfig();
}

static string HashUser(string userName)
{
    var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userName));
    return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
}

static Options ParseArgs(string[] args)
{
    var o = new Options();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out" when i + 1 < args.Length: o.OutFile = args[++i]; break;
            case "--delay" when i + 1 < args.Length: o.Delay = int.Parse(args[++i]); break;
            case "--baseline": o.Baseline = true; break;
            case "--bedrock": o.Bedrock = true; break;
            case "--upload":
                o.Upload = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) o.UploadUrl = args[++i];
                break;
            case "--spool-dir" when i + 1 < args.Length: o.SpoolDir = args[++i]; break;
        }
    }
    return o;
}

sealed class Options
{
    public string? OutFile { get; set; }
    public int Delay { get; set; } = 3;
    public bool Baseline { get; set; }
    public bool Bedrock { get; set; }
    public bool Upload { get; set; }
    public string? UploadUrl { get; set; }
    public string? SpoolDir { get; set; }
}
