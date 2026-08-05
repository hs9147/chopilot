namespace ChoPilot.Core;

// ─────────────────────────────────────────────────────────────────────────────
// 기반 정보 축 (ARCHITECTURE §5.4 4축 중 4번째).
//
// 다른 세 축과 성격이 다르다. 사용자·품목·도메인 축은 관측을 집계해서 지식을 "만든다".
// 기반 축은 만들 수 없다 — 거래처가 실재하는지, 그 날이 공휴일인지, 그 통화가 무엇인지는
// 우리 화면을 아무리 많이 봐도 알 수 없다. 관측이 할 수 있는 일은 <b>대사</b>뿐이다:
// 외부 권위 출처가 마스터를 주고, 관측은 그 마스터에 없는 것을 찾아낸다.
//
// 그래서 이 축의 데이터 계보는 한 방향으로 고정된다:
//   외부 출처 → 마스터 → (대사) → 관측
// 반대 방향은 금지다. 관측된 거래처 목록을 마스터로 승격하면 오타가 기준정보가 된다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 기반 사실의 종류. <see cref="Company"/>·<see cref="Item"/>은 <see cref="EntityKey.Type"/>과
/// <b>같은 문자열</b>이다 — 대사가 키 공간 비교로 성립하는 지점이라 일부러 맞춰 뒀다.
/// </summary>
public static class FoundationKind
{
    public const string Company = "Company";              // 거래처 마스터
    public const string Item = "Item";                    // 품목 마스터
    public const string Holiday = "Holiday";              // 공휴일 (납기일 계산의 전제)
    public const string Currency = "Currency";            // 통화 코드 (ISO 4217)
    public const string ExchangeRate = "ExchangeRate";    // 기준환율
    public const string UnitOfMeasure = "UnitOfMeasure";  // 계량 단위
}

/// <summary>
/// 기반 사실 1건. <see cref="Key"/>는 <see cref="EntityResolver.Normalize"/>를 거친 값이다 —
/// 관측 엔티티와 <b>같은 정규화</b>를 쓰지 않으면 " A사 "가 마스터의 "A사"와 어긋나
/// 멀쩡한 거래처가 전부 미등록으로 보고된다.
/// </summary>
public sealed record FoundationFact(
    string SourceId,
    string Kind,
    string Key,
    string Label,
    IReadOnlyDictionary<string, string> Attributes,
    DateTimeOffset? AsOf)
{
    /// <summary>키 길이 상한. 외부 응답이 본문에 그대로 실리므로 무한 길이를 받아 두지 않는다.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>속성 개수 상한. 출처가 무엇을 보내든 마스터의 모양은 우리가 정한다.</summary>
    public const int MaxAttributes = 20;

    /// <summary>정규화·절단을 거쳐 사실을 만든다. 키가 비면 <c>null</c> — 사실이 아니다.</summary>
    public static FoundationFact? Create(
        string sourceId, string kind, string rawKey, string? label,
        IReadOnlyDictionary<string, string>? attributes = null, DateTimeOffset? asOf = null)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return null;

        var key = EntityResolver.Normalize(rawKey);
        if (key.Length == 0) return null;
        if (key.Length > MaxKeyLength) key = key[..MaxKeyLength];

        var attrs = attributes is null
            ? EmptyAttributes
            : attributes.Take(MaxAttributes).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return new FoundationFact(sourceId, kind, key,
            string.IsNullOrWhiteSpace(label) ? rawKey.Trim() : label.Trim(), attrs, asOf);
    }

    public static IReadOnlyDictionary<string, string> EmptyAttributes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// 출처 1회 조회 결과. 실패도 결과다 — 조용히 빈 목록을 돌려주면 "마스터에 없다"와
/// "마스터를 못 받았다"가 구분되지 않고, 대사가 전부 미등록으로 뒤집힌다.
/// </summary>
public sealed record FoundationFetch(
    string SourceId,
    IReadOnlyList<FoundationFact> Facts,
    string? Error,
    DateTimeOffset FetchedAt)
{
    public bool Ok => Error is null;

    public static FoundationFetch Failed(string sourceId, string error, DateTimeOffset at) =>
        new(sourceId, Array.Empty<FoundationFact>(), error, at);
}

/// <summary>
/// 조회 요청. 관측된 키를 함께 넘긴다 — 목록형 출처(공휴일·환율)는 무시하지만,
/// 조회형 출처(국세청 사업자 상태)는 "무엇을 물어볼지"를 관측에서 받아야 한다.
/// 전량 덤프가 없는 무료 API를 쓰려면 이 방향이 유일하다.
/// </summary>
public sealed record FoundationQuery(IReadOnlyDictionary<string, IReadOnlyList<string>> ObservedKeys)
{
    public static FoundationQuery Empty { get; } =
        new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    public IReadOnlyList<string> For(string kind) =>
        ObservedKeys.TryGetValue(kind, out var keys) ? keys : Array.Empty<string>();
}

/// <summary>
/// 기반 정보 출처. 무료 공개 API, MCP 서버, 내장 표준 — 전부 이 뒤에 선다.
///
/// <para>
/// <see cref="RequiresNetwork"/>가 <c>false</c>인 출처는 <b>동기적으로 완료</b>해야 한다.
/// 부팅 시 마스터를 채우는 데 쓰이므로 여기서 I/O를 하면 시작이 네트워크에 묶인다.
/// </para>
/// </summary>
public interface IFoundationSource
{
    string Id { get; }
    string Title { get; }
    string Kind { get; }

    /// <summary>계보 표시용. "embedded" | "https://…" | "mcp:…" — 문서에 출처로 박힌다.</summary>
    string Origin { get; }

    /// <summary>이용 조건. 무료 여부와 재배포 가능성은 승인자가 볼 정보다.</summary>
    string License { get; }

    bool RequiresNetwork { get; }

    Task<FoundationFetch> FetchAsync(FoundationQuery query, CancellationToken ct = default);
}

/// <summary>
/// 병합된 기반 마스터 스냅숏. 같은 (Kind, Key)는 <b>나중 출처가 이긴다</b> —
/// 갱신 순서가 곧 우선순위이므로 권위 있는 출처를 뒤에 둔다.
/// </summary>
public sealed class FoundationMaster
{
    private readonly Dictionary<(string Kind, string Key), FoundationFact> _facts;

    public FoundationMaster(IEnumerable<FoundationFact> facts)
    {
        _facts = new Dictionary<(string, string), FoundationFact>();
        foreach (var f in facts) _facts[(f.Kind, f.Key)] = f;
    }

    public static FoundationMaster Empty { get; } = new(Array.Empty<FoundationFact>());

    public int Count => _facts.Count;

    public IReadOnlyList<string> Kinds =>
        _facts.Keys.Select(k => k.Kind).Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 이 종류의 마스터를 하나라도 가지고 있는가. 대사의 전제 조건이다 —
    /// 마스터가 비었는데 대사를 돌리면 관측 전량이 "미등록"으로 보고되어
    /// 경보가 전부 거짓이 된다.
    /// </summary>
    public bool Covers(string kind) => _facts.Keys.Any(k => k.Kind == kind);

    public int CountOf(string kind) => _facts.Keys.Count(k => k.Kind == kind);

    /// <summary>관측 엔티티와 같은 정규화로 조회한다.</summary>
    public FoundationFact? Lookup(string kind, string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return null;
        return _facts.TryGetValue((kind, EntityResolver.Normalize(rawKey)), out var fact) ? fact : null;
    }

    public IReadOnlyList<FoundationFact> OfKind(string kind) =>
        _facts.Values.Where(f => f.Kind == kind)
            .OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
}

/// <summary>
/// 내장 출처 — 네트워크 없이 항상 있는 표준 코드.
///
/// <para>
/// <b>여기에 거래처·품목은 없다.</b> 통화와 단위는 국제 표준이라 코드에 실어도 되지만,
/// 조직의 거래처 마스터는 우리가 알 수 없는 것이고 그럴듯한 목록을 심어 두면
/// 대사가 거짓 통과한다 — 빈 마스터는 <see cref="FoundationMaster.Covers"/>로 정직하게
/// "대사 불가"로 보고된다.
/// </para>
/// </summary>
public sealed class EmbeddedFoundationSource : IFoundationSource
{
    public string Id => "embedded.standards";
    public string Title => "내장 표준 코드 (통화·단위)";
    public string Kind => FoundationKind.Currency;
    public string Origin => "embedded";
    public string License => "ISO 4217 / UN-CEFACT 코드 — 공개 표준";
    public bool RequiresNetwork => false;

    private static readonly (string Code, string Name)[] Currencies =
    {
        ("KRW", "대한민국 원"), ("USD", "미국 달러"), ("EUR", "유로"),
        ("JPY", "일본 엔"), ("CNY", "중국 위안"),
    };

    private static readonly (string Code, string Name)[] Units =
    {
        ("EA", "개"), ("BOX", "박스"), ("SET", "세트"),
        ("KG", "킬로그램"), ("G", "그램"), ("M", "미터"), ("L", "리터"),
    };

    public Task<FoundationFetch> FetchAsync(FoundationQuery query, CancellationToken ct = default)
    {
        var facts = new List<FoundationFact>();

        foreach (var (code, name) in Currencies)
            if (FoundationFact.Create(Id, FoundationKind.Currency, code, name) is { } f) facts.Add(f);

        foreach (var (code, name) in Units)
            if (FoundationFact.Create(Id, FoundationKind.UnitOfMeasure, code, name) is { } f) facts.Add(f);

        // RequiresNetwork=false 계약: 동기 완료. 부팅 시 블로킹 없이 읽을 수 있어야 한다.
        return Task.FromResult(new FoundationFetch(Id, facts, null, DateTimeOffset.MinValue));
    }
}
