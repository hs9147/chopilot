using System.Net.Http.Json;

namespace ChoPilot.Core;

/// <summary>
/// 서버 호출 결과. <b>네트워크 예외와 비-2xx 응답이 모두 <c>Success=false</c>로 수렴</b>한다.
/// 호출측이 "예외가 났는가"가 아니라 "성공했는가"로 스풀 여부를 판정하게 하기 위함
/// (예외만 잡으면 5xx 응답이 성공으로 취급돼 이벤트가 유실된다).
/// </summary>
public sealed record ServerResponse(bool Success, int? StatusCode, string Detail)
{
    public override string ToString() =>
        StatusCode is { } code ? $"{code} {Detail}" : $"error {Detail}";
}

/// <summary>
/// 관측 이벤트를 서버로 업로드하고 Guide를 조회 (PHASE1-DESIGN §2.1 Uploader, §4).
/// PoC는 HTTPS; 운영은 gRPC/mTLS로 교체(ARCHITECTURE §3.1).
/// </summary>
public sealed class Uploader : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public Uploader(string baseUrl)
        : this(new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) },
               ownsClient: true)
    {
    }

    /// <summary>테스트에서 스텁 핸들러를 주입하기 위한 생성자.</summary>
    public Uploader(HttpClient http, bool ownsClient = false)
    {
        _http = http;
        _ownsClient = ownsClient;
    }

    public Task<ServerResponse> PostObservationAsync(ObservationEvent evt, CancellationToken ct = default) =>
        SendAsync(() => _http.PostAsJsonAsync("/v1/observations", evt, ct), ct);

    /// <summary>스풀 재전송용 어댑터 — 성공 여부만 반환.</summary>
    public async Task<bool> TryPostObservationAsync(ObservationEvent evt, CancellationToken ct = default) =>
        (await PostObservationAsync(evt, ct)).Success;

    public Task<ServerResponse> GetGuideAsync(string observationId, CancellationToken ct = default) =>
        SendAsync(() => _http.GetAsync($"/v1/guide?observation_id={Uri.EscapeDataString(observationId)}", ct), ct);

    private static async Task<ServerResponse> SendAsync(
        Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var resp = await send();
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new ServerResponse(resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        // 서버 다운·DNS 실패·타임아웃. 호출자가 취소한 경우는 삼키지 않고 전파한다.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ServerResponse(false, null, ex.Message);
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
