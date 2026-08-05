using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>출처 1개의 현재 상태. 오류도 상태다 — 실패한 출처는 화면에서 보여야 한다.</summary>
public sealed record FoundationSourceStatus(
    string Id,
    string Title,
    string Kind,
    string Origin,
    string License,
    bool RequiresNetwork,
    int Facts,
    string? Error,
    DateTimeOffset? FetchedAt);

/// <summary>
/// 기반 마스터 저장소 — 여러 출처의 사실을 하나의 스냅숏으로 병합한다.
///
/// <para>
/// 병합 우선순위는 <b>등록 순서</b>다(뒤에 등록된 출처가 이긴다). 조회 완료 순서로 하면
/// 같은 입력에 대해 마스터가 실행마다 달라진다 — 기준정보가 비결정적이면 대사도 비결정적이다.
/// </para>
/// <para>
/// PoC는 인메모리, 갱신은 수동 트리거. 운영은 일 배치 + Aurora 영속화(§11).
/// </para>
/// </summary>
public sealed class FoundationStore
{
    private readonly IReadOnlyList<IFoundationSource> _sources;
    private readonly object _gate = new();
    private readonly Dictionary<string, FoundationFetch> _fetches = new(StringComparer.Ordinal);
    private volatile FoundationMaster _master = FoundationMaster.Empty;

    public FoundationStore(IEnumerable<IFoundationSource> sources)
    {
        _sources = sources.ToList();

        // 네트워크가 필요 없는 출처는 부팅 시 즉시 적용한다. IFoundationSource 계약상
        // 이들은 동기 완료(Task.FromResult)이므로 GetResult가 블로킹하지 않는다 —
        // 여기서 I/O를 하는 출처가 생기면 서버 시작이 외부 API에 묶인다.
        foreach (var source in _sources.Where(s => !s.RequiresNetwork))
            Record(source.FetchAsync(FoundationQuery.Empty).GetAwaiter().GetResult());
    }

    /// <summary>읽기 경로가 쓰는 현재 마스터. 대사마다 조회 — 잠금 없이 읽는다.</summary>
    public FoundationMaster Master => _master;

    public IReadOnlyList<IFoundationSource> Sources => _sources;

    public IReadOnlyList<FoundationSourceStatus> Status()
    {
        lock (_gate)
        {
            return _sources.Select(s =>
            {
                var fetch = _fetches.GetValueOrDefault(s.Id);
                return new FoundationSourceStatus(
                    s.Id, s.Title, s.Kind, s.Origin, s.License, s.RequiresNetwork,
                    fetch?.Facts.Count ?? 0, fetch?.Error,
                    fetch is null || fetch.FetchedAt == DateTimeOffset.MinValue ? null : fetch.FetchedAt);
            }).ToList();
        }
    }

    /// <summary>마지막으로 성공한 조회. 출처 등록 문서의 근거가 된다.</summary>
    public FoundationFetch? LastFetch(string sourceId)
    {
        lock (_gate) return _fetches.GetValueOrDefault(sourceId);
    }

    /// <summary>
    /// 모든 출처를 갱신한다. <b>어떤 출처가 실패해도 던지지 않는다</b> — 하나가 죽었다고
    /// 나머지 마스터를 버리면 대사가 통째로 멎는다. 실패는 상태로 남아 화면에 뜬다.
    /// </summary>
    public async Task<IReadOnlyList<FoundationFetch>> RefreshAsync(
        FoundationQuery query, CancellationToken ct = default)
    {
        var results = new List<FoundationFetch>();

        foreach (var source in _sources)
        {
            FoundationFetch fetch;
            try
            {
                fetch = await source.FetchAsync(query, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                fetch = FoundationFetch.Failed(source.Id, $"{ex.GetType().Name}: {ex.Message}",
                    DateTimeOffset.UtcNow);
            }

            Record(fetch);
            results.Add(fetch);
        }

        return results;
    }

    /// <summary>
    /// 조회 결과를 적재하고 마스터를 재구성한다.
    /// <b>실패한 조회는 직전 사실을 지우지 않는다</b> — 일시적 장애로 마스터가 비면
    /// 관측 전량이 미등록으로 보고되어 경보가 뒤집힌다.
    /// </summary>
    private void Record(FoundationFetch fetch)
    {
        lock (_gate)
        {
            if (fetch.Ok || !_fetches.ContainsKey(fetch.SourceId))
                _fetches[fetch.SourceId] = fetch;
            else
                _fetches[fetch.SourceId] = _fetches[fetch.SourceId] with
                {
                    Error = fetch.Error,
                    FetchedAt = fetch.FetchedAt,
                };

            // 등록 순서대로 다시 쌓는다 — 뒤 출처가 앞 출처를 덮는다.
            _master = new FoundationMaster(
                _sources.Select(s => _fetches.GetValueOrDefault(s.Id))
                        .Where(f => f is not null)
                        .SelectMany(f => f!.Facts));
        }
    }

    /// <summary>
    /// 관측된 엔티티 키를 조회 질의로 바꾼다. 전량 덤프가 없는 무료 API를 쓰려면
    /// "우리가 본 것"만 물어보는 수밖에 없다.
    /// </summary>
    public static FoundationQuery QueryFrom(EntityStore entities) =>
        new(entities.All()
            .GroupBy(e => e.Type, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(e => e.Key).ToList(),
                StringComparer.Ordinal));
}
