using System.Text.Json;
using ChoPilot.Client;
using ChoPilot.Core;
using ChoPilot.Mapping;

// ─────────────────────────────────────────────────────────────────────────────
// chopilot-dump — Phase 0 UIA 관측 실측 도구 (PHASE0-PLAN WS2/WS3)
//
//   사용법:
//     chopilot-dump [--out <파일>] [--delay <초>] [--baseline]
//
//   동작:
//     1. --delay 초 대기 (그 사이 관측할 브라우저 화면을 포그라운드로)
//     2. 포커스된 창의 접근성 트리를 정규화 수집
//     3. PrivacyGate 적용(마스킹) → 화면 서명 계산
//     4. --baseline 이면 StubAiMapper로 alias 매핑까지 시도
//     5. ObservationEvent JSON을 stdout + (옵션)파일로 출력
//
//   산출물은 PHASE0-KIT §2(관측 인벤토리)·§3(매핑) 측정의 원자료가 된다.
// ─────────────────────────────────────────────────────────────────────────────

var opts = ParseArgs(args);

if (opts.Delay > 0)
{
    Console.Error.WriteLine($"[chopilot-dump] {opts.Delay}s 후 포커스 창을 캡처합니다. 대상 화면을 앞으로 두세요...");
    await Task.Delay(TimeSpan.FromSeconds(opts.Delay));
}

using var observer = new UiaObserver();
var (screen, rawTree) = observer.CaptureForegroundWindow();

var gate = new PrivacyGate();
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
Console.Error.WriteLine($"[chopilot-dump] nodes captured, masked refs = {maskedRefs.Count}");

if (opts.Baseline)
{
    var mapper = new StubAiMapper();
    var inference = await mapper.InferAsync("PurchaseRequest", maskedTree, ProcurementOntology.Concepts);
    Console.Error.WriteLine($"[chopilot-dump] baseline(stub) mapped {inference.Fields.Count} fields:");
    foreach (var f in inference.Fields)
        Console.Error.WriteLine($"    {f.ElementRef} -> {f.Concept} (conf {f.Confidence})");
}

var output = JsonSerializer.Serialize(new { signature, observation = evt }, jsonOpts);
Console.WriteLine(output);

if (opts.OutFile is not null)
{
    await File.WriteAllTextAsync(opts.OutFile, output);
    Console.Error.WriteLine($"[chopilot-dump] wrote {opts.OutFile}");
}

return 0;

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
        }
    }
    return o;
}

sealed class Options
{
    public string? OutFile { get; set; }
    public int Delay { get; set; } = 3;
    public bool Baseline { get; set; }
}
