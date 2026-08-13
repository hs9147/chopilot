using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using ChoPilot.Client;
using ChoPilot.Core;
using ChoPilot.Mapping;
using Microsoft.Extensions.Configuration;

// ─────────────────────────────────────────────────────────────────────────────
// chopilot-dump — UIA 관측 도구 (PHASE0-PLAN WS2/WS3)
//
//   사용법:
//     chopilot-dump [--out <파일>] [--delay <초>] [--baseline] [--bedrock]
//                   [--upload [url]] [--spool-dir <경로>] [--completed]
//                   [--watch [초]] [--rounds <n>] [--resend-unchanged]
//
//   두 가지 모드:
//     한 번   (기본)      : 한 화면을 찍고 끝난다.
//     자동    (--watch)   : 정해진 간격으로 계속 찍는다. Ctrl+C로 중단.
//                           원클릭 실행은 chopilot-watch.cmd 를 더블클릭.
//
//   동작:
//     1. --delay 초 대기 (그 사이 관측할 화면을 포그라운드로)
//     2. 포그라운드 창의 접근성 트리를 정규화 수집 + 화면/레코드 식별(ScreenIdentifier)
//     3. ConsentPolicy(on/off·앱별 제외) → PrivacyGate(마스킹) → 화면 서명 계산
//     4. --baseline : StubAiMapper(alias) 매핑 시도 (대조군)
//        --bedrock  : Bedrock 동적 매핑 시도 (실 AI, appsettings 설정 사용)
//                     자동 모드에서는 첫 회차에만 돈다 — 매 회차 호출하면 비용이 시간에 비례한다
//        --upload [url] : 서버로 POST 후 Guide 조회 (기본 Server:IngestionEndpoint)
//                         전송 실패 시 durable 스풀에 적재, 다음 회차에서 재전송
//        --spool-dir    : 스풀 디렉터리 지정 (기본 <실행경로>/spool)
//        --completed    : 이 캡처를 작업 완료 신호로 표시 (저장 버튼을 누른 직후 캡처)
//                         완료 시점의 화면만이 "이 업무객체가 실제로 무엇을 요구했는가"의
//                         증거가 된다 — 작성 중간의 빈칸은 증거가 아니다 (ARCHITECTURE §5.7)
//        --trigger <v>  : 트리거를 직접 지정 (focus_changed|structure_changed|save_clicked)
//        --watch [초]   : 자동 반복 관측 (기본 10초). --completed 와 함께 쓸 수 없다
//        --rounds <n>   : 자동 모드를 n회차만 돌린다 (기본: 무제한)
//        --resend-unchanged : 화면이 그대로여도 매 회차 전송한다 (기본은 건너뜀)
//     5. ObservationEvent JSON을 stdout + (옵션)파일로 출력 (자동 모드에서는 stdout 생략)
//
//   설정: appsettings.json → appsettings.local.json → 환경변수(CHOPILOT_*) 순 오버라이드
// ─────────────────────────────────────────────────────────────────────────────

var cfg = LoadConfig();
var opts = ParseArgs(args);

// 오타 난 트리거는 서버가 400으로 거부한다 — 캡처를 마친 뒤에 알게 되면 그 관측은 버려진다.
if (!ObservationTrigger.IsValid(opts.Trigger))
{
    Console.Error.WriteLine($"[chopilot-dump] 알 수 없는 --trigger '{opts.Trigger}' — " +
        $"{ObservationTrigger.FocusChanged}|{ObservationTrigger.StructureChanged}|{ObservationTrigger.SaveClicked}");
    return 1;
}

// 완료 신호는 "저장을 눌렀다"는 <b>1회성 판단</b>이다. 반복 모드에서 매 회차에 붙이면
// 작성 중간 화면이 전부 완료로 기록되고, 필수 필드 규칙의 증거가 통째로 오염된다.
if (opts.Watch is not null && ObservationTrigger.IsCompletion(opts.Trigger))
{
    Console.Error.WriteLine("[chopilot-dump] --completed 는 --watch 와 함께 쓸 수 없다 — " +
        "반복 캡처를 전부 완료로 기록하면 필수 필드 규칙의 증거가 오염된다. " +
        "저장 직후 한 번만 따로 실행하라.");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (opts.Delay > 0)
{
    Console.Error.WriteLine($"[chopilot-dump] {opts.Delay}s 후 포그라운드 창을 캡처합니다. 대상 화면을 앞으로 두세요...");
    try { await Task.Delay(TimeSpan.FromSeconds(opts.Delay), cts.Token); }
    catch (OperationCanceledException) { return 0; }
}

using var observer = new UiaObserver();
var gate = new PrivacyGate(policyVersion: cfg.Privacy.PolicyVersion);
var consentPolicy = new ConsentPolicy(cfg.Consent);
var changes = new ChangeDetector();
var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

// 업로드 자원은 <b>회차마다 만들지 않는다</b> — HttpClient를 반복 생성하면 소켓이 고갈된다.
var url = opts.UploadUrl
    ?? (string.IsNullOrWhiteSpace(cfg.Server.IngestionEndpoint) ? "http://127.0.0.1:5080" : cfg.Server.IngestionEndpoint);
var spoolDir = opts.SpoolDir ?? Path.Combine(AppContext.BaseDirectory, "spool");
var spool = opts.Upload ? new EventSpool(spoolDir) : null;
using var uploader = opts.Upload ? new Uploader(url) : null;

if (opts.Upload)
    Console.Error.WriteLine($"[chopilot-dump] upload → {url} (spool: {spoolDir}, 대기 {spool!.PendingCount})");

var captured = 0;

if (opts.Watch is { } intervalSeconds)
{
    Console.Error.WriteLine($"[chopilot-dump] 자동 관측 시작 — {intervalSeconds}초 간격"
        + (opts.Rounds is { } n ? $", {n}회차" : ", Ctrl+C로 중단")
        + (opts.SkipUnchanged ? ", 변화 없으면 건너뜀" : ", 변화 없어도 전송"));

    var loop = new ObservationLoop(
        TimeSpan.FromSeconds(intervalSeconds),
        RoundAsync,
        (round, result) => Console.Error.WriteLine($"[chopilot-dump] #{round} {Describe(result)}"));

    var stats = await loop.RunAsync(opts.Rounds, cts.Token);
    Console.Error.WriteLine($"[chopilot-dump] 종료 — {stats}");
    return 0;
}

var single = await RoundAsync(cts.Token);
Console.Error.WriteLine($"[chopilot-dump] {Describe(single)}");
return single.Outcome == RoundOutcome.Failed ? 1 : 0;

// ── 관측 1회차 ───────────────────────────────────────────────────────────────

async Task<ObservationRound> RoundAsync(CancellationToken ct)
{
    var (screen, rawTree) = observer.CaptureForegroundWindow(cfg.Observation.MaxDepth, cfg.Observation.MaxNodes);

    // 동의 게이트는 <b>매 회차</b> 다시 평가한다. 한 번만 보고 반복하면, 사용자가 도중에
    // 제외 대상 앱으로 옮겨가도 계속 캡처된다 — 그건 동의가 아니다.
    var consent = consentPolicy.Evaluate(screen);
    if (!consent.Allowed) return new ObservationRound(RoundOutcome.Blocked, consent.Reason);

    var (maskedTree, maskedRefs) = gate.Apply(rawTree);

    // 값까지 보는 지문. 아무 일도 없었으면 보내지 않는다 — 같은 화면을 반복 적재하면
    // 감사 로그와 적중률 분모가 부풀어 학습이 아니라 반복을 센 결과가 된다.
    if (opts.SkipUnchanged && !changes.HasChanged(screen, maskedTree))
        return new ObservationRound(RoundOutcome.Unchanged, SignatureService.NormalizeRoute(screen.Url));

    captured++;
    var signature = SignatureService.Compute(screen, maskedTree);

    var evt = new ObservationEvent(
        EventId: Guid.NewGuid().ToString(),
        SessionId: Environment.MachineName,
        UserId: HashUser(Environment.UserName),
        CapturedAt: DateTimeOffset.Now,
        Screen: screen,
        Tree: maskedTree,
        Privacy: new PrivacyInfo(gate.PolicyVersion, maskedRefs),
        Trigger: opts.Trigger);

    if (opts.Watch is null)
    {
        Console.Error.WriteLine($"[chopilot-dump] signature = {signature}");
        if (ObservationTrigger.IsCompletion(opts.Trigger))
            Console.Error.WriteLine("[chopilot-dump] 완료 신호 — 이 화면 상태가 필수 필드 규칙의 증거가 됩니다");
        Console.Error.WriteLine($"[chopilot-dump] masked refs = {maskedRefs.Count}");
    }

    // 대조군·Bedrock은 진단이다. 자동 모드에서 매 회차 돌리면 비용이 시간에 비례한다.
    if (captured == 1 || opts.Watch is null)
    {
        if (opts.Baseline) await RunMapper("baseline(stub)", new StubAiMapper(), maskedTree);
        if (opts.Bedrock)
        {
            Console.Error.WriteLine($"[chopilot-dump] Bedrock: region={cfg.Aws.Region}, model={cfg.Aws.BedrockModelId}");
            var region = RegionEndpoint.GetBySystemName(cfg.Aws.Region);
            using var bedrock = new AmazonBedrockRuntimeClient(region);
            await RunMapper("bedrock(ai)", new BedrockAiMapper(bedrock, cfg.Aws.BedrockModelId), maskedTree);
        }
    }

    var output = JsonSerializer.Serialize(new { signature, observation = evt }, jsonOpts);

    // 자동 모드에서 stdout 덤프는 소방호스가 된다 — 파일로만 남긴다.
    if (opts.Watch is null) Console.WriteLine(output);

    if (OutPath(captured) is { } path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, output, ct);
        if (opts.Watch is null) Console.Error.WriteLine($"[chopilot-dump] wrote {path}");
    }

    if (spool is null || uploader is null)
        return new ObservationRound(RoundOutcome.Captured, Short(signature));

    // 밀린 스풀 재전송 → 현재 이벤트 전송.
    // 재시도 가능한 실패는 유실 대신 스풀에 남고, 서버가 영구히 거부한 것(4xx)은 격리된다 —
    // 거부를 남기면 큐 머리를 막아 그 뒤의 모든 관측이 서버에 도달하지 못한다.
    var dispatch = await ObservationDispatcher.DispatchAsync(
        spool, evt, e => uploader.SendObservationAsync(e, ct), ct);

    var extra = (dispatch.Drained > 0 ? $" · 스풀 재전송 {dispatch.Drained}" : "")
              + (dispatch.Rejected > 0 ? $" · 스풀 격리 {dispatch.Rejected}" : "");

    if (dispatch.Sent)
    {
        if (opts.Watch is null)
            Console.Error.WriteLine("[chopilot-dump] guide : " + await uploader.GetGuideAsync(evt.EventId, ct));
        return new ObservationRound(RoundOutcome.Sent, Short(signature) + extra);
    }

    // 거부는 되돌아오지 않는다. 다음 회차가 같은 화면을 다시 시도하도록 지문을 지운다 —
    // 지우지 않으면 "변화 없음"으로 건너뛰어 고친 뒤에도 영영 올라가지 않는다.
    changes.Reset();

    return dispatch.Outcome == SendOutcome.Rejected
        ? new ObservationRound(RoundOutcome.Rejected, Short(signature) + extra)
        : new ObservationRound(RoundOutcome.Spooled, $"{Short(signature)} · 대기 {dispatch.Pending}{extra}");
}

// ── 보조 ────────────────────────────────────────────────────────────────────

// 자동 모드에서 --out 은 회차마다 새 파일이다. 덮어쓰면 마지막 한 장만 남아
// "시간에 따라 무엇이 변했는가"를 보려던 이유가 사라진다.
string? OutPath(int index) =>
    opts.OutFile is null ? null
    : opts.Watch is null ? opts.OutFile
    : Path.Combine(
        Path.GetDirectoryName(opts.OutFile) is { Length: > 0 } dir ? dir : ".",
        $"{Path.GetFileNameWithoutExtension(opts.OutFile)}-{index:D3}{Path.GetExtension(opts.OutFile)}");

static string Short(string signature)
{
    var body = signature.Split(':').Last();
    return body.Length <= 12 ? body : body[..12];
}

static string Describe(ObservationRound r) => r.Outcome switch
{
    RoundOutcome.Sent => $"전송 · {r.Detail}",
    RoundOutcome.Captured => $"캡처만 · {r.Detail}",
    RoundOutcome.Spooled => $"전송 실패 → 스풀 · {r.Detail}",
    RoundOutcome.Rejected => $"서버 거부(4xx) — 스풀에 넣지 않는다 · {r.Detail}",
    RoundOutcome.Unchanged => $"변화 없음 — 건너뜀 · {r.Detail}",
    RoundOutcome.Blocked => $"관측 중단 — {r.Detail}",
    _ => $"실패 · {r.Detail}",
};

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
            case "--trigger" when i + 1 < args.Length: o.Trigger = args[++i]; break;
            case "--completed": o.Trigger = ObservationTrigger.SaveClicked; break;
            case "--watch":
                // 값은 선택이다. 붙이지 않으면 측정 콘솔의 자동 반복과 같은 10초.
                o.Watch = i + 1 < args.Length && !args[i + 1].StartsWith("--")
                    ? Math.Clamp(int.Parse(args[++i]), Options.MinWatchSeconds, Options.MaxWatchSeconds)
                    : Options.DefaultWatchSeconds;
                break;
            case "--rounds" when i + 1 < args.Length: o.Rounds = Math.Max(1, int.Parse(args[++i])); break;
            case "--resend-unchanged": o.SkipUnchanged = false; break;
        }
    }
    return o;
}

sealed class Options
{
    public const int DefaultWatchSeconds = 10;
    public const int MinWatchSeconds = 1;
    public const int MaxWatchSeconds = 3600;

    public string? OutFile { get; set; }
    public int Delay { get; set; } = 3;
    public bool Baseline { get; set; }
    public bool Bedrock { get; set; }
    public bool Upload { get; set; }
    public string? UploadUrl { get; set; }
    public string? SpoolDir { get; set; }

    /// <summary>이 캡처를 일으킨 상호작용. 기본은 화면 전환 관측이다.</summary>
    public string Trigger { get; set; } = ObservationTrigger.FocusChanged;

    /// <summary>자동 반복 간격(초). null이면 한 번만 찍는다.</summary>
    public int? Watch { get; set; }

    /// <summary>자동 모드 회차 상한. null이면 취소될 때까지.</summary>
    public int? Rounds { get; set; }

    /// <summary>화면이 그대로면 전송하지 않는다. 자동 모드의 기본값.</summary>
    public bool SkipUnchanged { get; set; } = true;
}
