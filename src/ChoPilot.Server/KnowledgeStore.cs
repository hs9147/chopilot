using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>
/// 지식 문서 1건의 상태 전이. 여기만 <b>연산</b>을 남기고 다른 저장소는 레코드를 남긴다 —
/// 승인·폐기는 대기본을 <b>지우는</b> 부수효과가 있어서 문서만 봐서는 복원할 수 없다.
/// </summary>
public sealed record KnowledgeChange(string Op, KnowledgeDoc Doc)
{
    public const string Submitted = "submit";
    public const string Approved = "approve";
    public const string Deprecated = "deprecate";
}

/// <summary>
/// Curated Knowledge Plane 저장소 (ARCHITECTURE §5.4 Plane 3, §5.5).
/// 문서 수명주기: 제출(pending) → 승인(published) → 폐기(deprecated). <b>삭제는 없다.</b>
/// 게시·폐기마다 지식 버전이 오르고 읽기 경로 산출물이 재컴파일된다.
/// PoC는 인메모리 + 코드 시드. 운영은 Aurora(버전·감사 이력 포함, §11).
/// </summary>
public sealed class KnowledgeStore : IKnowledgeProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, KnowledgeDoc> _docs = new();      // id → published/deprecated
    private readonly Dictionary<string, KnowledgeDoc> _pending = new();   // id → 승인 대기 (게시본과 공존 가능 = 개정)
    private readonly IJournal<KnowledgeChange> _journal;
    private volatile CompiledKnowledge _compiled;
    private int _version;

    public KnowledgeStore(IJournalFactory? journals = null)
    {
        _journal = (journals ?? NullJournalFactory.Instance).Open<KnowledgeChange>("knowledge");

        // 시드를 먼저 깔고 저널을 그 위에 얹는다 — 시드 문서의 개정본이 시드를 덮는다.
        foreach (var doc in KnowledgeSeed.Documents) _docs[doc.Id] = doc;
        _version = 1;

        foreach (var change in _journal.Load()) Replay(change);

        // 재컴파일은 마지막에 한 번. 복원 중간 상태를 읽는 경로는 없다.
        _compiled = KnowledgeCompiler.Compile(_docs.Values, _version);
    }

    /// <summary>
    /// 저널 1건 반영. 버전은 <b>게시·폐기 횟수만큼</b> 오른다 — 재시작 후 버전이 되감기면
    /// 저신뢰 매핑의 재추론 백오프가 "온톨로지가 바뀌었다"고 잘못 판단한다(§5.5 7단계).
    /// </summary>
    private void Replay(KnowledgeChange change)
    {
        switch (change.Op)
        {
            case KnowledgeChange.Submitted:
                _pending[change.Doc.Id] = change.Doc;
                break;

            case KnowledgeChange.Approved:
            case KnowledgeChange.Deprecated:
                _docs[change.Doc.Id] = change.Doc;
                _pending.Remove(change.Doc.Id);
                _version++;
                break;
        }
    }

    /// <summary>읽기 경로가 소비하는 현재 컴파일 스냅숏. 관측당 조회 — 잠금 없이 읽는다.</summary>
    public CompiledKnowledge Current => _compiled;

    public IReadOnlyList<KnowledgeDoc> List(string? axis = null, string? status = null)
    {
        lock (_gate)
        {
            return _docs.Values.Concat(_pending.Values)
                .Where(d => axis is null || d.Axis == axis)
                .Where(d => status is null || d.Status == status)
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>승인 대기본이 있으면 그것을, 없으면 게시/폐기본을 돌려준다.</summary>
    public KnowledgeDoc? Get(string id)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(id, out var pending) ? pending
                 : _docs.TryGetValue(id, out var doc) ? doc
                 : null;
        }
    }

    /// <summary>
    /// 이 id의 승인 대기 개정본. 개정을 제안하는 집계기가 <b>초안을 쌓지 않기</b> 위해 쓴다 —
    /// <see cref="Get"/>는 대기본과 게시본을 구분해 주지 않으므로 "이미 존재"로는 판단할 수 없다.
    /// </summary>
    public KnowledgeDoc? PendingDraft(string id)
    {
        lock (_gate) return _pending.GetValueOrDefault(id);
    }

    /// <summary>
    /// 초안 제출 → pending_review. 같은 Id의 게시본이 있으면 <b>개정</b>이 된다
    /// (승인 시 게시본을 대체, 버전 +1). 이미 대기 중인 초안은 덮어쓴다(초안 편집).
    /// </summary>
    public (KnowledgeDoc? Doc, string? Error) Submit(KnowledgeDoc doc, string createdBy, DateTimeOffset at)
    {
        if (Validate(doc) is { } error) return (null, error);

        lock (_gate)
        {
            var published = _docs.TryGetValue(doc.Id, out var existing) ? existing : null;
            var draft = doc with
            {
                Kind = KnowledgeKind.Curated,          // 제출 경로로는 curated만 — view는 재생성 파생물이다
                Status = KnowledgeStatus.PendingReview,
                Version = (published?.Version ?? 0) + 1,
                CreatedBy = createdBy,
                ApprovedBy = null,
                UpdatedAt = at,
            };
            _pending[doc.Id] = draft;
            _journal.Append(new KnowledgeChange(KnowledgeChange.Submitted, draft));
            return (draft, null);
        }
    }

    /// <summary>
    /// 승인 → 게시 + 재컴파일 + 버전 증가.
    /// 민감 개념의 민감 → 비민감 <b>하향은 거부</b>한다: 통과시키면 이후 관측에서 그 개념의
    /// 값이 마스킹되지 않은 채 흐른다 — 보정의 미지 개념 거부와 같은 방어선이다.
    /// </summary>
    public (KnowledgeDoc? Doc, string? Error) Approve(string id, string approvedBy, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out var draft))
                return (null, "승인 대기 중인 문서가 없다");

            if (draft.Concept is { Sensitive: false } &&
                _docs.TryGetValue(id, out var prior) && prior.Concept is { Sensitive: true })
                return (null, "민감 개념의 비민감 하향은 허용되지 않는다 — 폐기 후 새 개념으로 등록하라");

            var published = draft with { Status = KnowledgeStatus.Published, ApprovedBy = approvedBy, UpdatedAt = at };
            _pending.Remove(id);
            _docs[id] = published;
            _journal.Append(new KnowledgeChange(KnowledgeChange.Approved, published));
            Recompile();
            return (published, null);
        }
    }

    /// <summary>폐기 + 재컴파일 + 버전 증가. 문서는 이력으로 남는다.</summary>
    public (KnowledgeDoc? Doc, string? Error) Deprecate(string id, string actor, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_docs.TryGetValue(id, out var doc))
                return (null, "게시된 문서가 없다");
            if (doc.Status == KnowledgeStatus.Deprecated)
                return (null, "이미 폐기된 문서다");

            var deprecated = doc with { Status = KnowledgeStatus.Deprecated, ApprovedBy = actor, UpdatedAt = at };
            _docs[id] = deprecated;
            _pending.Remove(id);   // 폐기 대상의 대기 개정도 함께 무효
            _journal.Append(new KnowledgeChange(KnowledgeChange.Deprecated, deprecated));
            Recompile();
            return (deprecated, null);
        }
    }

    private void Recompile() => _compiled = KnowledgeCompiler.Compile(_docs.Values, ++_version);

    private static string? Validate(KnowledgeDoc doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Id)) return "id가 비어 있다";
        if (doc.Id.Contains('/')) return "id에 '/'는 허용되지 않는다 (경로 세그먼트로 쓰인다)";
        if (!KnowledgeAxis.IsValid(doc.Axis)) return $"axis '{doc.Axis}'는 user|item|domain|foundation 중 하나여야 한다";
        if (!KnowledgeType.IsValid(doc.Type)) return $"type '{doc.Type}'는 concept|required_fields|business_hint|note 중 하나여야 한다";

        // 타입별 페이로드 정합 — 스키마 없는 자유 지식은 컴파일할 수 없다
        return doc.Type switch
        {
            KnowledgeType.Concept when doc.Concept is null => "concept 문서에는 concept 페이로드가 필요하다",
            KnowledgeType.RequiredFields when doc.Required is null => "required_fields 문서에는 required 페이로드가 필요하다",
            KnowledgeType.BusinessHint when doc.Hint is null => "business_hint 문서에는 hint 페이로드가 필요하다",
            _ => null,
        };
    }
}
