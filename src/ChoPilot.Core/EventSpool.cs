using System.Text.Json;

namespace ChoPilot.Core;

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

    public EventSpool(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>스풀에 대기 중인 이벤트 수.</summary>
    public int PendingCount => SpoolFiles().Count();

    /// <summary>이벤트를 durable 스풀에 적재.</summary>
    public void Enqueue(ObservationEvent evt)
    {
        var name = $"{evt.CapturedAt.UtcTicks:D19}_{evt.EventId}.json";
        var tmp = Path.Combine(_dir, name + ".tmp");
        var final = Path.Combine(_dir, name);

        // 원자적 쓰기: tmp에 완전히 쓴 뒤 rename → 부분 파일(반쪽 JSON) 방지.
        File.WriteAllText(tmp, JsonSerializer.Serialize(evt, Json));
        File.Move(tmp, final, overwrite: true);
    }

    /// <summary>
    /// 스풀을 오래된 순으로 비운다. sender가 true(전송 성공)를 반환한 이벤트만 삭제.
    /// 실패(false/예외)하면 그 이벤트는 남기고 중단(순서 보존, 다음 기회 재시도).
    /// </summary>
    /// <returns>성공적으로 전송·삭제된 이벤트 수.</returns>
    public async Task<int> DrainAsync(Func<ObservationEvent, Task<bool>> sender, CancellationToken ct = default)
    {
        var sent = 0;
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
                continue;
            }
            if (evt is null) { Quarantine(file); continue; }

            bool ok;
            try { ok = await sender(evt); }
            catch { ok = false; }

            if (!ok) break;            // 전송 실패 → 순서 보존 위해 중단
            File.Delete(file);
            sent++;
        }
        return sent;
    }

    private IEnumerable<string> SpoolFiles() =>
        Directory.Exists(_dir)
            ? Directory.EnumerateFiles(_dir, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    private void Quarantine(string file)
    {
        try { File.Move(file, file + ".bad", overwrite: true); }
        catch { /* best-effort */ }
    }
}
