using System.Collections.Concurrent;
using ChoPilot.Core;
using ChoPilot.Mapping;

namespace ChoPilot.Server;

public sealed record StoredObservation(ObservationEvent Event, MappingEntry Entry, BusinessObject BusinessObject);

/// <summary>PoC용 인메모리 관측 저장소. 운영은 Aurora/S3 등으로 교체(ARCHITECTURE §2).</summary>
public sealed class ObservationStore
{
    private readonly ConcurrentDictionary<string, StoredObservation> _store = new();

    public void Put(string id, ObservationEvent evt, MappingEntry entry, BusinessObject bo) =>
        _store[id] = new StoredObservation(evt, entry, bo);

    public StoredObservation? Get(string id) =>
        _store.TryGetValue(id, out var v) ? v : null;
}

/// <summary>화면 URL/타이틀에서 업무객체 힌트 추정 (PoC 규칙).</summary>
public static class BusinessHint
{
    public static string FromScreen(ScreenInfo screen)
    {
        var text = $"{screen.Url} {screen.Title}".ToLowerInvariant();
        if (text.Contains("/po") || text.Contains("발주") || text.Contains("order"))
            return "PurchaseOrder";
        return "PurchaseRequest";
    }
}
