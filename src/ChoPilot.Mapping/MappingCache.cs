using System.Collections.Concurrent;
using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>App Adapter Registry 추상화 (ARCHITECTURE §3.2). 운영은 DynamoDB, PoC는 InMemory.</summary>
public interface IMappingCache
{
    MappingEntry? Get(string signature, string scope);
    void Put(MappingEntry entry);

    /// <summary>전체 엔트리 열람 (검수 큐·운영 진단용). 운영 DynamoDB는 scan/GSI로 대체.</summary>
    IEnumerable<MappingEntry> All();
}

/// <summary>
/// 인메모리 캐시 + 선택적 저널. 저널을 주면 재시작에도 살아남는다 —
/// 이 캐시를 잃는다는 것은 <b>이미 치른 AI 추론 비용을 다시 치른다</b>는 뜻이다.
/// 키가 엔트리에서 유도되므로 복원은 마지막 쓰기가 이기는 것으로 충분하다(로그 구조 저장소).
/// </summary>
public sealed class InMemoryMappingCache : IMappingCache
{
    private readonly ConcurrentDictionary<string, MappingEntry> _store = new();
    private readonly IJournal<MappingEntry> _journal;

    public InMemoryMappingCache(IJournalFactory? journals = null)
    {
        _journal = (journals ?? NullJournalFactory.Instance).Open<MappingEntry>("mappings");
        foreach (var entry in _journal.Load()) _store[Key(entry.Signature, entry.Scope)] = entry;
    }

    private static string Key(string signature, string scope) => $"{scope}::{signature}";

    public MappingEntry? Get(string signature, string scope) =>
        _store.TryGetValue(Key(signature, scope), out var e) ? e : null;

    public void Put(MappingEntry entry)
    {
        _store[Key(entry.Signature, entry.Scope)] = entry;
        _journal.Append(entry);
    }

    public IEnumerable<MappingEntry> All() => _store.Values;

    public int Count => _store.Count;
}
