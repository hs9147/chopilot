using System.Text.Json;

namespace ChoPilot.Core;

/// <summary>
/// 한 이벤트의 전송 결과. <b>실패를 둘로 나누는 것</b>이 핵심이다 —
/// 스풀이 FIFO를 지키느라 실패한 이벤트를 큐 머리에 남기기 때문에,
/// 영원히 실패할 이벤트와 나중에 성공할 이벤트를 같이 취급하면 큐가 통째로 막힌다.
/// </summary>
public enum SendOutcome
{
    /// <summary>서버가 받았다 → 스풀에서 지운다.</summary>
    Sent,

    /// <summary>지금은 안 되지만 나중엔 될 수 있다(오프라인·5xx·타임아웃) → 남긴다.</summary>
    Retry,

    /// <summary>서버가 영구히 거부했다(4xx 계약 위반) → 격리한다. 다시 보내도 결과는 같다.</summary>
    Rejected,
}

/// <summary>스풀 배출 결과. 격리 건수를 함께 돌려준다 — 침묵하는 폐기는 전송으로 읽힌다.</summary>
public sealed record DrainResult(int Sent, int Rejected);
public sealed record SpoolStatus(int Pending, long PendingBytes, int Quarantined);

public sealed record EventSpoolOptions(
    int MaxEvents = 10_000,
    long MaxBytes = 512L * 1024 * 1024,
    TimeSpan? Retention = null)
{
    public TimeSpan EffectiveRetention => Retention ?? TimeSpan.FromDays(7);
}

/// <summary>
/// Durable 로컬 이벤트 스풀 (ARCHITECTURE §3.1 Event Buffer, PHASE1-DESIGN §2.1/§7 가용성 NFR).
/// 영속 VDI 디스크에 이벤트를 파일로 적재 → 서버 장애·오프라인 시에도 유실 없이 재전송.
///  - 파일명 = CapturedAt UtcTicks + EventId → 오래된 이벤트부터 정렬(FIFO)
///  - 전송 성공분만 삭제(at-least-once). 크로스플랫폼·단위 테스트 가능.
/// </summary>
public sealed class EventSpool
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _dir;
    private readonly EventSpoolOptions _options;
    private long _sequence;

    public EventSpool(string directory, EventSpoolOptions? options = null)
    {
        _dir = directory;
        _options = options ?? new EventSpoolOptions();
        Directory.CreateDirectory(_dir);
        _sequence = SpoolFiles().Select(ParseSequence).DefaultIfEmpty(0).Max();
        TrimExpired();
    }

    /// <summary>스풀에 대기 중인 이벤트 수.</summary>
    public int PendingCount => SpoolFiles().Count();
    public SpoolStatus Status => new(PendingCount, SpoolFiles().Sum(FileSize), QuarantinedFiles().Count());

    /// <summary>이벤트를 durable 스풀에 적재.</summary>
    public void Enqueue(ObservationEvent evt)
    {
        var payload = JsonSerializer.Serialize(evt, Json);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        if (bytes > _options.MaxBytes)
            throw new InvalidOperationException("event payload exceeds spool quota");
        EnsureCapacity(bytes);

        var sequence = Interlocked.Increment(ref _sequence);
        // event_id는 파일명에 직접 넣지 않는다. 계약 바깥 입력도 경로를 벗어날 수 없다.
        var eventToken = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(evt.EventId)))[..16].ToLowerInvariant();
        var name = sequence.ToString("D19") + "_" + eventToken + ".json";
        var tmp = Path.Combine(_dir, name + ".tmp");
        var final = Path.Combine(_dir, name);

        // 원자적 쓰기: tmp에 완전히 쓴 뒤 rename → 부분 파일(반쪽 JSON) 방지.
        File.WriteAllText(tmp, payload);
        File.Move(tmp, final, overwrite: true);
    }

    /// <summary>
    /// 성공/실패만 아는 호출자용 어댑터. <b>실패를 전부 재시도로 본다</b> —
    /// 영구 거부를 구분하려면 <see cref="SendOutcome"/> 오버로드를 써야 한다.
    /// </summary>
    public Task<DrainResult> DrainAsync(Func<ObservationEvent, Task<bool>> sender, CancellationToken ct = default) =>
        DrainAsync(async evt => await sender(evt) ? SendOutcome.Sent : SendOutcome.Retry, ct);

    /// <summary>
    /// 스풀을 오래된 순으로 비운다.
    ///
    /// <list type="bullet">
    ///   <item><see cref="SendOutcome.Sent"/> — 삭제하고 다음으로.</item>
    ///   <item><see cref="SendOutcome.Retry"/> — 남기고 <b>중단</b>(순서 보존, 다음 기회 재시도).</item>
    ///   <item><see cref="SendOutcome.Rejected"/> — 격리하고 <b>계속</b>.</item>
    /// </list>
    ///
    /// <para>
    /// 마지막 항목이 이 메서드의 존재 이유다. 서버가 영구히 거부하는 이벤트를 재시도로
    /// 취급하면 그것이 큐 머리에 눌러앉아 <b>그 뒤의 모든 관측이 서버에 도달하지 못한다</b> —
    /// 손상 파일을 격리하는 것과 같은 이유이고, 실패 모드는 더 조용하다(파일은 멀쩡하다).
    /// </para>
    /// </summary>
    public async Task<DrainResult> DrainAsync(
        Func<ObservationEvent, Task<SendOutcome>> sender, CancellationToken ct = default)
    {
        var sent = 0;
        var rejected = 0;

        foreach (var file in SpoolFiles())
        {
            ct.ThrowIfCancellationRequested();

            ObservationEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ObservationEvent>(File.ReadAllText(file), Json);
            }
            catch
            {
                // 손상 파일은 격리(무한 재시도 방지).
                Quarantine(file);
                rejected++;
                continue;
            }
            if (evt is null) { Quarantine(file); rejected++; continue; }

            SendOutcome outcome;
            try { outcome = await sender(evt); }
            catch { outcome = SendOutcome.Retry; }

            switch (outcome)
            {
                case SendOutcome.Sent:
                    File.Delete(file);
                    sent++;
                    break;

                case SendOutcome.Rejected:
                    Quarantine(file);
                    rejected++;
                    break;

                default:
                    return new DrainResult(sent, rejected);   // 재시도 가능 → 순서 보존 위해 중단
            }
        }

        return new DrainResult(sent, rejected);
    }

    private IEnumerable<string> SpoolFiles() =>
        Directory.Exists(_dir)
            ? Directory.EnumerateFiles(_dir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    private IEnumerable<string> QuarantinedFiles() =>
        Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, "*.bad") : Enumerable.Empty<string>();

    private void EnsureCapacity(long incomingBytes)
    {
        var pending = SpoolFiles().ToList();
        var pendingBytes = pending.Sum(FileSize);
        if (pending.Count + 1 <= _options.MaxEvents && pendingBytes + incomingBytes <= _options.MaxBytes) return;

        // 관측은 같은 우선순위다. 가장 오래된 파일을 격리하고 상태로 노출한다.
        // Iterate a snapshot: capacity may require evicting more than one file.
        // Mutating `pending` while enumerating it would fail in that case.
        foreach (var file in pending.ToList())
        {
            var size = FileSize(file);
            Quarantine(file);
            pendingBytes -= size;
            if (pending.Count - 1 <= _options.MaxEvents && pendingBytes + incomingBytes <= _options.MaxBytes)
                return;
            pending.Remove(file);
        }
        throw new InvalidOperationException("spool quota cannot accommodate event");
    }

    private void TrimExpired()
    {
        var threshold = DateTimeOffset.UtcNow - _options.EffectiveRetention;
        foreach (var file in SpoolFiles())
            if (File.GetLastWriteTimeUtc(file) < threshold.UtcDateTime)
                Quarantine(file);
    }

    private static long ParseSequence(string path)
    {
        var token = Path.GetFileName(path).Split('_', 2)[0];
        return long.TryParse(token, out var sequence) ? sequence : 0;
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private void Quarantine(string file)
    {
        try { File.Move(file, file + ".bad", overwrite: true); }
        catch { /* best-effort */ }
    }
}
