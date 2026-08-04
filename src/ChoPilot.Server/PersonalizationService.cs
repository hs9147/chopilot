using ChoPilot.Core;
using ChoPilot.Mapping;

namespace ChoPilot.Server;

public sealed record CorrectionField(string ElementRef, string Concept);

/// <summary>
/// 개인 보정 요청 (PHASE1-DESIGN §4.2·§5.4 피드백 루프). 사용자가 매핑을 정정한다.
/// <b>사용자 식별자는 본문에 없다</b> — 요청 헤더에서만 온다(<see cref="RequestUser"/>).
/// </summary>
public sealed record CorrectionRequest(
    string Signature,
    string BusinessObject,
    List<CorrectionField> Mapping,
    RecordHint? RecordId = null);

/// <summary>저신뢰 매핑 승격 요청 (ARCHITECTURE §5.2 step 6 HITL).</summary>
public sealed record PromoteRequest(string Signature, string Scope, double? Confidence = null);

/// <summary>
/// 보정 처리 결과. 온톨로지에 없는 개념이 하나라도 있으면 <b>전체를 거부</b>한다.
/// </summary>
public sealed record CorrectionOutcome(MappingEntry? Entry, List<string> UnknownConcepts)
{
    public bool Accepted => Entry is not null;
}

/// <summary>
/// 개인화·검수(HITL) 로직 (ARCHITECTURE §5.2·§5.4). Program.cs를 얇게 유지하고 단위 테스트 가능하게 분리.
///  - 검수 큐   : 저신뢰(pending_review) 매핑 열람
///  - 승격      : 검수 통과분을 trusted로 (HITL)
///  - 개인 보정 : personal 스코프에 사용자 정정 매핑 적재 → 캐스케이드 우선 적용(D5)
/// </summary>
public sealed class PersonalizationService
{
    private readonly IMappingCache _cache;

    public PersonalizationService(IMappingCache cache) => _cache = cache;

    /// <summary>
    /// 검수 대기(저신뢰) 매핑 목록. <b>개인 스코프는 제외</b>한다 —
    /// 검수 큐는 여러 사람이 함께 보는 화면이라 한 사용자의 개인 매핑이 실리면 격리가 깨진다.
    /// </summary>
    public IReadOnlyList<MappingEntry> ReviewQueue(int limit = 100) =>
        _cache.All()
            .Where(e => e.Status == "pending_review")
            .Where(e => !e.Scope.StartsWith("personal:", StringComparison.Ordinal))
            .OrderBy(e => e.Confidence)      // 낮은 신뢰도부터(우선 검수)
            .Take(limit)
            .ToList();

    /// <summary>검수 통과분을 trusted로 승격. 대상이 없으면 null.</summary>
    public MappingEntry? Promote(PromoteRequest req)
    {
        var entry = _cache.Get(req.Signature, req.Scope);
        if (entry is null) return null;

        var promoted = entry with
        {
            Status = "trusted",
            Confidence = req.Confidence ?? entry.Confidence,
        };
        _cache.Put(promoted);
        return promoted;
    }

    /// <summary>
    /// 사용자 정정 매핑을 personal 스코프에 적재. confidence=1.0, provenance="user".
    /// 이후 동일 (signature, user) 해석은 캐스케이드에서 personal이 우선 적중(D5).
    ///
    /// <para>
    /// 개념은 이름뿐 아니라 <b>별칭으로도</b> 받는다 — 사용자는 화면에 보이는 "단가"로 정정하지
    /// "UnitPrice"로 정정하지 않는다. 반대로 <b>해석되지 않는 개념은 거부</b>한다:
    /// 그 개념의 민감 여부를 알 수 없는데 통과시키면 <c>Sensitive=false</c>로 굳어져,
    /// 신뢰도 1.0·personal 스코프로 캐스케이드 1순위를 영구히 차지하면서
    /// Business Object의 민감값 억제(2차 방어선)를 무력화한다.
    /// </para>
    /// </summary>
    public CorrectionOutcome ApplyCorrection(string userId, CorrectionRequest req)
    {
        var resolved = req.Mapping
            .Select(m => (Field: m, Concept: ProcurementOntology.Resolve(m.Concept)))
            .ToList();

        var unknown = resolved.Where(r => r.Concept is null)
                              .Select(r => r.Field.Concept)
                              .Distinct()
                              .ToList();
        if (unknown.Count > 0) return new CorrectionOutcome(null, unknown);

        var fields = resolved.Select(r => new FieldMapping(
            ElementRef: r.Field.ElementRef,
            Concept: r.Concept!.Name,          // 별칭으로 들어와도 정규 개념명으로 저장
            Confidence: 1.0,
            Provenance: "user",
            Sensitive: r.Concept.Sensitive)).ToList();

        var entry = new MappingEntry(
            Signature: req.Signature,
            Scope: $"personal:{userId}",
            UserId: userId,
            BusinessObject: req.BusinessObject,
            RecordId: req.RecordId,
            Mapping: fields,
            Confidence: 1.0,
            Status: "trusted");

        _cache.Put(entry);
        return new CorrectionOutcome(entry, unknown);
    }
}
