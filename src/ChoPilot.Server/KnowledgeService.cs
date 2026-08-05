using ChoPilot.Core;
using ChoPilot.Mapping;

namespace ChoPilot.Server;

/// <summary>폐기 결과 — 문서와 그 여파(강등된 매핑 수)를 함께 보고한다.</summary>
public sealed record DeprecateOutcome(KnowledgeDoc? Doc, string? Error, int TouchedMappings);

/// <summary>
/// 지식 수명주기와 매핑 캐시의 정합 (ARCHITECTURE §5.5 7단계).
/// 개념 <b>추가</b>는 기존 매핑에 손대지 않는다. 개념 <b>폐기</b>는 그 개념을 쓰는 매핑에서
/// 해당 필드를 제거한다 — 폐기된 개념으로의 매핑은 더 이상 유효한 지식이 아니기 때문이다.
/// </summary>
public sealed class KnowledgeService
{
    private readonly KnowledgeStore _store;
    private readonly IMappingCache _cache;
    private readonly double _thetaHigh;

    public KnowledgeService(KnowledgeStore store, IMappingCache cache, double thetaHigh = 0.8)
    {
        _store = store;
        _cache = cache;
        _thetaHigh = thetaHigh;
    }

    public (KnowledgeDoc? Doc, string? Error) Submit(KnowledgeDoc doc, string createdBy) =>
        _store.Submit(doc, createdBy, DateTimeOffset.UtcNow);

    public (KnowledgeDoc? Doc, string? Error) Approve(string id, string approvedBy) =>
        _store.Approve(id, approvedBy, DateTimeOffset.UtcNow);

    /// <summary>
    /// 폐기. 개념 문서였다면 캐시를 순회해 그 개념의 필드 매핑을 제거하고 신뢰도를 재계산한다.
    /// 남은 필드의 신뢰도가 θ 미만으로 떨어지면 pending_review로 강등 —
    /// 다음 관측에서 재추론(지식 버전이 이미 올라 백오프도 만료돼 있다) 또는 검수 대상이 된다.
    /// </summary>
    public DeprecateOutcome Deprecate(string id, string actor)
    {
        var (doc, error) = _store.Deprecate(id, actor, DateTimeOffset.UtcNow);
        if (doc is null) return new DeprecateOutcome(null, error, 0);
        if (doc.Concept is not { } concept) return new DeprecateOutcome(doc, null, 0);

        var touched = 0;
        foreach (var entry in _cache.All())
        {
            if (!entry.Mapping.Any(m => m.Concept.Equals(concept.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var remaining = entry.Mapping
                .Where(m => !m.Concept.Equals(concept.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var confidence = remaining.Count == 0 ? 0 : remaining.Average(f => f.Confidence);

            _cache.Put(entry with
            {
                Mapping = remaining,
                Confidence = confidence,
                Status = confidence >= _thetaHigh ? entry.Status : "pending_review",
            });
            touched++;
        }

        return new DeprecateOutcome(doc, null, touched);
    }
}
