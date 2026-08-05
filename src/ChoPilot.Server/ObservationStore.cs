using System.Collections.Concurrent;
using ChoPilot.Core;
using ChoPilot.Mapping;

namespace ChoPilot.Server;

public sealed record StoredObservation(
    long Seq, string ObservationId, ObservationEvent Event, MappingEntry Entry, BusinessObject BusinessObject);

/// <summary>PoC용 인메모리 관측 저장소. 운영은 Aurora/S3 등으로 교체(ARCHITECTURE §2).</summary>
public sealed class ObservationStore
{
    private readonly ConcurrentDictionary<string, StoredObservation> _store = new();
    private long _seq;

    public void Put(string id, ObservationEvent evt, MappingEntry entry, BusinessObject bo) =>
        _store[id] = new StoredObservation(Interlocked.Increment(ref _seq), id, evt, entry, bo);

    public StoredObservation? Get(string id) =>
        _store.TryGetValue(id, out var v) ? v : null;

    /// <summary>적재 순서대로. 측정 UI가 스냅샷 목록을 그리는 데 쓴다.</summary>
    public IReadOnlyList<StoredObservation> List() =>
        _store.Values.OrderBy(v => v.Seq).ToList();
}

// 화면→업무객체 힌트는 하드코딩(구 BusinessHint)에서 지식 문서로 이전됐다 —
// CompiledKnowledge.ResolveBusinessHint(ARCHITECTURE §5.5, business_hint 문서).
