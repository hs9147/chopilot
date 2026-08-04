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

public sealed class InMemoryMappingCache : IMappingCache
{
    private readonly ConcurrentDictionary<string, MappingEntry> _store = new();

    private static string Key(string signature, string scope) => $"{scope}::{signature}";

    public MappingEntry? Get(string signature, string scope) =>
        _store.TryGetValue(Key(signature, scope), out var e) ? e : null;

    public void Put(MappingEntry entry) =>
        _store[Key(entry.Signature, entry.Scope)] = entry;

    public IEnumerable<MappingEntry> All() => _store.Values;

    public int Count => _store.Count;
}
