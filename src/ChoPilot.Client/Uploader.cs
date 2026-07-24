using System.Net.Http.Json;
using ChoPilot.Core;

namespace ChoPilot.Client;

/// <summary>
/// 관측 이벤트를 서버로 업로드하고 Guide를 조회 (PHASE1-DESIGN §2.1 Uploader, §4).
/// PoC는 HTTPS; 운영은 gRPC/mTLS로 교체(ARCHITECTURE §3.1).
/// </summary>
public sealed class Uploader : IDisposable
{
    private readonly HttpClient _http;

    public Uploader(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<string> PostObservationAsync(ObservationEvent evt, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/v1/observations", evt, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return $"{(int)resp.StatusCode} {body}";
    }

    public async Task<string> GetGuideAsync(string observationId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(
            $"/v1/guide?observation_id={Uri.EscapeDataString(observationId)}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return $"{(int)resp.StatusCode} {body}";
    }

    public void Dispose() => _http.Dispose();
}
