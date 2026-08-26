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
/// AI 추정 이력 한 페이지. <paramref name="Total"/>은 <b>자르기 전</b> 개수다 —
/// 몇 건이 잘렸는지 말해 주지 않으면 화면이 "이게 전부"라고 잘못 말한다.
/// </summary>
public sealed record InferenceLedger(IReadOnlyList<MappingEntry> Entries, int Total);

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
    private readonly IKnowledgeProvider _knowledge;
    private readonly UnknownConceptLog _unknownConcepts;

    public PersonalizationService(IMappingCache cache, IKnowledgeProvider knowledge,
                                  UnknownConceptLog? unknownConcepts = null)
    {
        _cache = cache;
        _knowledge = knowledge;
        _unknownConcepts = unknownConcepts ?? new UnknownConceptLog();
    }

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

    /// <summary>
    /// AI 추정 이력 — 지금 캐시에 서 있는 판단 전부를 <b>최근 추론순</b>으로.
    ///
    /// <para>
    /// 검수 큐와 다르다. 검수 큐는 "손봐야 하는 것"만(θ 미만) 신뢰도 낮은 순으로 주는 작업 목록이고,
    /// 이쪽은 <b>trusted까지 포함한 대장</b>이다. 승격·보정으로 큐에서 빠진 판단은 큐에서 사라지지만
    /// 계속 쓰이므로, 그것까지 보이지 않으면 "AI가 무엇을 결정해 두었나"를 볼 방법이 없다.
    /// </para>
    /// <para>
    /// 개인 스코프는 <b>본인 것만</b> 보인다. 공용 평면은 모두가 보지만, 남의 개인 매핑이 실리면
    /// 검수 큐에서 막아 둔 격리가 이 목록으로 새는 것과 같다.
    /// </para>
    /// </summary>
    public InferenceLedger Inferences(string userId, int limit = 200)
    {
        var visible = _cache.All()
            .Where(e => !e.Scope.StartsWith("personal:", StringComparison.Ordinal)
                        || e.Scope == PersonalScope(userId))
            // 사람이 만든 매핑(보정·승격)은 LastInferredAt이 null이다 — 맨 뒤가 아니라 맨 앞이어야
            // "방금 내가 고친 것"이 보인다. 그래서 null을 현재로 취급한다.
            .OrderByDescending(e => e.LastInferredAt ?? DateTimeOffset.MaxValue)
            .ToList();

        // 잘린 사실을 함께 돌려준다. 침묵하는 절단은 "이게 전부"로 읽힌다 —
        // 대장에서 그 오해는 "AI가 결정해 둔 것은 이것뿐"이라는 잘못된 확신이 된다.
        return new InferenceLedger(visible.Take(limit).ToList(), visible.Count);
    }

    /// <summary>
    /// 추정 제외 — 캐시에서 지운다. 지운 엔트리를 돌려주고, 없거나 권한이 없으면 null.
    ///
    /// <para>
    /// 되돌리는 것이 아니라 <b>다시 묻게 하는</b> 것이다. 엔트리가 사라지면 재추론 백오프의 기준도
    /// 함께 사라지므로 다음 관측에서 곧바로 AI를 다시 부른다 — <b>추론 비용이 다시 난다</b>.
    /// 판단이 틀렸을 때 쓰는 것이지, 마음에 안 들 때 누르는 버튼이 아니다.
    /// </para>
    /// <para>
    /// 남의 개인 스코프는 지울 수 없다. 공용 평면은 누구든 지울 수 있고 결정 이력에 남는다 —
    /// 공용 판단을 지우는 것은 모두에게 영향이 가므로 누가 했는지가 증거로 남아야 한다.
    /// </para>
    /// </summary>
    public MappingEntry? Discard(string signature, string scope, string userId)
    {
        if (scope.StartsWith("personal:", StringComparison.Ordinal) && scope != PersonalScope(userId))
            return null;

        var entry = _cache.Get(signature, scope);
        if (entry is null) return null;

        _cache.Remove(signature, scope);
        return entry;
    }

    private static string PersonalScope(string userId) => $"personal:{userId}";

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
        // 온톨로지는 하드코딩이 아니라 현재 게시된 지식이다 — 개념 문서가 승인되면
        // 재배포 없이 그 개념으로의 보정이 즉시 가능해진다(ARCHITECTURE §5.5).
        var ontology = _knowledge.Current;
        var resolved = req.Mapping
            .Select(m => (Field: m, Concept: ontology.Resolve(m.Concept)))
            .ToList();

        var unknown = resolved.Where(r => r.Concept is null)
                              .Select(r => r.Field.Concept)
                              .Distinct()
                              .ToList();
        if (unknown.Count > 0)
        {
            // 거부하되 버리지 않는다 — 이 시도가 온톨로지 결핍의 증거다(ARCHITECTURE §5.5 Signal).
            _unknownConcepts.Record(userId, req.Signature, req.BusinessObject, unknown, DateTimeOffset.UtcNow);
            return new CorrectionOutcome(null, unknown);
        }

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
