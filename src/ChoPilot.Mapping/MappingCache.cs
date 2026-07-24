using System.Collections.Concurrent;
using ChoPilot.Core;

namespace ChoPilot.Mapping;

/// <summary>App Adapter Registry 추상화 (ARCHITECTURE §3.2). 운영은 DynamoDB, PoC는 InMemory.</summary>
public interface IMappingCache
{
    MappingEntry? Get(string signature, string scope);
    void Put(MappingEntry entry);
}

public sealed class InMemoryMappingCache : IMappingCache
{
    private readonly ConcurrentDictionary<string, MappingEntry> _store = new();

    private static string Key(string signature, string scope) => $"{scope}::{signature}";

    public MappingEntry? Get(string signature, string scope) =>
        _store.TryGetValue(Key(signature, scope), out var e) ? e : null;

    public void Put(MappingEntry entry) =>
        _store[Key(entry.Signature, entry.Scope)] = entry;

    public int Count => _store.Count;
}
