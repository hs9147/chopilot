namespace ChoPilot.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Curated Knowledge Plane (ARCHITECTURE §5.4 Plane 3, §5.5).
// 온톨로지·업무 규칙을 코드가 아니라 "버전 관리되는 문서"로 다룬다.
// 지식의 성장이 배포 주기에서 분리되는 지점이다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>지식 문서의 관측 축 (4축 생성 모델).</summary>
public static class KnowledgeAxis
{
    public const string User = "user";              // 개인 파생 뷰
    public const string Item = "item";              // 업무(품목) 특성
    public const string Domain = "domain";          // 도메인(구매) 프로세스·개념·규칙
    public const string Foundation = "foundation";  // 기반 정보 (시드 + 관측 검증)

    public static bool IsValid(string axis) =>
        axis is User or Item or Domain or Foundation;
}

/// <summary>문서 종류. view는 스토어에서 재생성되는 파생물 — 편집도 승인도 없다.</summary>
public static class KnowledgeKind
{
    public const string View = "view";
    public const string Curated = "curated";
}

/// <summary>문서 타입 — 컴파일러가 무엇으로 바꾸는지를 결정한다.</summary>
public static class KnowledgeType
{
    public const string Concept = "concept";                // → 온톨로지 개념
    public const string RequiredFields = "required_fields"; // → Guide 필수 필드 규칙
    public const string BusinessHint = "business_hint";     // → 화면→업무객체 힌트
    public const string Note = "note";                      // 서술만 (컴파일 대상 아님)

    public static bool IsValid(string type) =>
        type is Concept or RequiredFields or BusinessHint or Note;
}

public static class KnowledgeStatus
{
    public const string PendingReview = "pending_review";
    public const string Published = "published";
    public const string Deprecated = "deprecated";
}

/// <summary>업무객체별 필수 개념 규칙 (기존 GuideService.RequiredByBo의 문서화).</summary>
public sealed record RequiredFieldsRule(string BusinessObject, string[] Concepts);

/// <summary>
/// 화면 → 업무객체 힌트 규칙. URL·타이틀에 <see cref="Keywords"/> 중 하나가 포함되면
/// <see cref="BusinessObject"/>로 판정. Keywords가 비어 있으면 <b>기본값</b> 규칙이다.
/// </summary>
public sealed record BusinessHintRule(string[] Keywords, string BusinessObject);

/// <summary>
/// 문서의 출처 — 어떤 관측 신호에서 왔고 얼마나 지지되는가.
/// <see cref="DistinctUsers"/>는 org 승격 게이트다: 한 사람에게서만 관측된 패턴을
/// org로 올리면 그 사람의 활동이 유출된다(ARCHITECTURE §5.4 승격 사다리).
/// </summary>
public sealed record KnowledgeProvenance(
    List<string> SignalRefs,
    int SupportCount,
    int DistinctUsers,
    DateTimeOffset? LastObserved)
{
    public static KnowledgeProvenance Seed { get; } = new(new List<string> { "seed" }, 0, 0, null);
}

/// <summary>
/// 지식 문서 1건. <b>본문에는 관측된 값이 들어가지 않는다</b> — 구조·의미·규칙만.
/// 타입별 페이로드는 그 타입일 때만 채워진다(다른 타입이면 null).
/// </summary>
public sealed record KnowledgeDoc(
    string Id,                       // "concept.UnitPrice", "rule.required.PurchaseRequest"
    string Axis,
    string Kind,
    string Type,
    string Scope,                    // D5 재사용: "personal:{userId}" | "org:{orgId}" | "global"
    string Title,
    Concept? Concept,                // Type=concept
    RequiredFieldsRule? Required,    // Type=required_fields
    BusinessHintRule? Hint,          // Type=business_hint
    string Body,
    int Version,
    string Status,
    KnowledgeProvenance Provenance,
    string CreatedBy,                // "seed" | "aggregator" | "llm-editor" | 사용자
    string? ApprovedBy,              // curated 게시엔 필수 — null인 published는 불변식 위반
    DateTimeOffset UpdatedAt);

/// <summary>
/// 게시된 지식의 컴파일 결과 — 읽기 경로가 소비하는 형태 (ARCHITECTURE §5.5 6단계).
/// 관측당 처리에서 LLM도 문서 파싱도 없다: 스냅숏 하나를 조회할 뿐이다.
/// </summary>
public sealed record CompiledKnowledge(
    int Version,
    Concept[] Concepts,
    IReadOnlyDictionary<string, string[]> RequiredByBusinessObject,
    IReadOnlyList<BusinessHintRule> Hints)
{
    /// <summary>개념 <b>이름</b>만 정확일치. 별칭은 보지 않는다.</summary>
    public Concept? ByName(string name) =>
        Array.Find(Concepts, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 이름 <b>또는 별칭</b>으로 조회 — 사람 입력용. 해석 실패 시 호출측은 거부해야 한다:
    /// 미지 개념의 민감 여부를 알 수 없기 때문이다(Ontology.cs의 원칙 그대로).
    /// </summary>
    public Concept? Resolve(string? nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias)) return null;
        var needle = nameOrAlias.Trim();

        return ByName(needle)
            ?? Array.Find(Concepts, c =>
                   c.Aliases.Any(a => a.Equals(needle, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>화면 URL·타이틀에서 업무객체 힌트 판정 (구 BusinessHint.FromScreen).</summary>
    public string ResolveBusinessHint(ScreenInfo screen)
    {
        var text = $"{screen.Url} {screen.Title}".ToLowerInvariant();

        foreach (var rule in Hints)
        {
            if (rule.Keywords.Length == 0) continue;   // 기본값 규칙은 마지막에
            if (rule.Keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return rule.BusinessObject;
        }

        return Hints.FirstOrDefault(r => r.Keywords.Length == 0)?.BusinessObject
            ?? "PurchaseRequest";
    }

    /// <summary>업무객체의 필수 개념. 규칙이 없으면 매핑된 개념 전체를 필수로 간주(호출측 폴백).</summary>
    public string[]? RequiredFor(string businessObject) =>
        RequiredByBusinessObject.TryGetValue(businessObject, out var required) ? required : null;
}

/// <summary>현재 컴파일 스냅숏 제공자. 서버에선 KnowledgeStore가 구현한다.</summary>
public interface IKnowledgeProvider
{
    CompiledKnowledge Current { get; }
}

/// <summary>게시된 문서 집합 → 읽기 경로 산출물 컴파일 (ARCHITECTURE §5.5 6단계).</summary>
public static class KnowledgeCompiler
{
    public static CompiledKnowledge Compile(IEnumerable<KnowledgeDoc> docs, int version)
    {
        var published = docs.Where(d => d.Status == KnowledgeStatus.Published).ToList();

        var concepts = published
            .Where(d => d.Type == KnowledgeType.Concept && d.Concept is not null)
            .Select(d => d.Concept!)
            .ToArray();

        var required = published
            .Where(d => d.Type == KnowledgeType.RequiredFields && d.Required is not null)
            .ToDictionary(d => d.Required!.BusinessObject, d => d.Required!.Concepts);

        // 구체 규칙(키워드 있음) 먼저, 기본값 규칙은 뒤로 — 판정 순서가 곧 우선순위다.
        var hints = published
            .Where(d => d.Type == KnowledgeType.BusinessHint && d.Hint is not null)
            .Select(d => d.Hint!)
            .OrderBy(h => h.Keywords.Length == 0 ? 1 : 0)
            .ToList();

        return new CompiledKnowledge(version, concepts, required, hints);
    }
}

/// <summary>
/// 초기 시드 — 기존에 코드에 하드코딩되어 있던 지식의 문서화.
/// <see cref="ProcurementOntology"/>는 이제 이 시드의 원료일 뿐, 런타임의 진실이 아니다.
/// </summary>
public static class KnowledgeSeed
{
    public static IReadOnlyList<KnowledgeDoc> Documents { get; } = Build();

    /// <summary>시드만으로 컴파일한 스냅숏 (버전 1). 테스트·기본 동작의 기준선.</summary>
    public static CompiledKnowledge Compile() => KnowledgeCompiler.Compile(Documents, version: 1);

    private static List<KnowledgeDoc> Build()
    {
        var docs = new List<KnowledgeDoc>();

        foreach (var concept in ProcurementOntology.Concepts)
        {
            docs.Add(Curated(
                id: $"concept.{concept.Name}",
                type: KnowledgeType.Concept,
                title: $"개념: {concept.Name}",
                body: $"구매 도메인 공통 개념. 별칭: {string.Join(", ", concept.Aliases)}",
                concept: concept));
        }

        docs.Add(Curated(
            id: "rule.required.PurchaseRequest",
            type: KnowledgeType.RequiredFields,
            title: "구매요청 필수 필드",
            body: "구매요청 작성이 완료되려면 채워져야 하는 개념.",
            required: new RequiredFieldsRule("PurchaseRequest",
                new[] { "Material", "Quantity", "DeliveryDate", "Vendor" })));

        docs.Add(Curated(
            id: "rule.required.PurchaseOrder",
            type: KnowledgeType.RequiredFields,
            title: "발주 필수 필드",
            body: "발주 작성이 완료되려면 채워져야 하는 개념.",
            required: new RequiredFieldsRule("PurchaseOrder",
                new[] { "OrderNo", "Vendor", "TotalAmount" })));

        docs.Add(Curated(
            id: "hint.PurchaseOrder",
            type: KnowledgeType.BusinessHint,
            title: "발주 화면 판정",
            body: "URL·타이틀에 발주 관련 키워드가 있으면 PurchaseOrder로 판정.",
            hint: new BusinessHintRule(new[] { "/po", "발주", "order" }, "PurchaseOrder")));

        docs.Add(Curated(
            id: "hint.default",
            type: KnowledgeType.BusinessHint,
            title: "기본 업무객체",
            body: "다른 힌트에 걸리지 않는 화면의 기본값.",
            hint: new BusinessHintRule(Array.Empty<string>(), "PurchaseRequest")));

        return docs;
    }

    private static KnowledgeDoc Curated(
        string id, string type, string title, string body,
        Concept? concept = null, RequiredFieldsRule? required = null, BusinessHintRule? hint = null) => new(
        Id: id,
        Axis: KnowledgeAxis.Domain,
        Kind: KnowledgeKind.Curated,
        Type: type,
        Scope: "global",
        Title: title,
        Concept: concept,
        Required: required,
        Hint: hint,
        Body: body,
        Version: 1,
        Status: KnowledgeStatus.Published,
        Provenance: KnowledgeProvenance.Seed,
        CreatedBy: "seed",
        ApprovedBy: "seed",
        UpdatedAt: DateTimeOffset.MinValue);   // 시드는 시각이 없다 — 저장소 적재 시각과 무관
}
