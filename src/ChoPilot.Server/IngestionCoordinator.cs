using System.Collections.Concurrent;

namespace ChoPilot.Server;

/// <summary>
/// 동일 tenant/user/event의 동시·재시도 처리를 직렬화한다.
/// 저장된 관측 영수증을 확인하는 코드는 lease 안에서 실행해야 중복 AI 호출과 파생 누적을 막는다.
/// </summary>
public sealed class IngestionCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> AcquireAsync(
        string tenantId, string userId, string eventId, CancellationToken ct = default)
    {
        var key = $"{tenantId}\u001f{userId}\u001f{eventId}";
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
