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
    private readonly IJournal<StoredObservation> _journal;
    private long _seq;

    public ObservationStore(IJournalFactory? journals = null)
    {
        _journal = (journals ?? NullJournalFactory.Instance).Open<StoredObservation>("observations");

        // 같은 id가 다시 나오면 나중 것이 이긴다 — 살아 있는 경로의 덮어쓰기와 같은 규칙.
        foreach (var stored in _journal.Load())
        {
            _store[stored.ObservationId] = stored;
            if (stored.Seq > _seq) _seq = stored.Seq;
        }
    }

    public void Put(string id, ObservationEvent evt, MappingEntry entry, BusinessObject bo)
    {
        var stored = new StoredObservation(Interlocked.Increment(ref _seq), id, evt, entry, bo);
        _store[id] = stored;
        _journal.Append(stored);
    }

    public StoredObservation? Get(string id) =>
        _store.TryGetValue(id, out var v) ? v : null;

    /// <summary>적재 순서대로. 측정 UI가 스냅샷 목록을 그리는 데 쓴다.</summary>
    public IReadOnlyList<StoredObservation> List() =>
        _store.Values.OrderBy(v => v.Seq).ToList();
}

// 화면→업무객체 힌트는 하드코딩(구 BusinessHint)에서 지식 문서로 이전됐다 —
// CompiledKnowledge.ResolveBusinessHint(ARCHITECTURE §5.5, business_hint 문서).
