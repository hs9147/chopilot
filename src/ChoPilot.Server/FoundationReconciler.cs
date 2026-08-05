using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>대사 판정. <b>미등록과 대사 불가는 다른 사실이다</b> — 섞으면 경보가 무의미해진다.</summary>
public static class ReconcileStatus
{
    public const string Matched = "matched";              // 마스터에 있다
    public const string Unmatched = "unmatched";          // 마스터에 없다 — 이것만 경보다
    public const string Unverifiable = "unverifiable";    // 키 공간이 달라 물어볼 수 없다
    public const string NoMaster = "no_master";           // 이 종류의 출처가 아직 없다
}

public sealed record ReconcileRow(
    string Kind,
    string Key,
    int Mentions,
    int DistinctUsers,
    DateTimeOffset LastSeen,
    string Status,
    string? Detail);

public sealed record ReconcileReport(
    int Checked,
    int Matched,
    int Unmatched,
    int Unverifiable,
    int NoMaster,
    IReadOnlyList<ReconcileRow> Rows,
    IReadOnlyList<string> Notes)
{
    public static ReconcileReport EmptyReport { get; } =
        new(0, 0, 0, 0, 0, Array.Empty<ReconcileRow>(), Array.Empty<string>());
}

/// <summary>
/// 관측 ↔ 기반 마스터 대사 (기반 정보 축의 본체).
///
/// <para>
/// 계보는 한 방향이다: <b>외부 출처 → 마스터 → 대사 → 관측</b>. 관측된 거래처 목록을
/// 마스터로 승격하는 경로는 없다 — 있으면 화면의 오타가 기준정보가 되고, 그 뒤로는
/// 어떤 대사도 그 오타를 통과시킨다.
/// </para>
/// <para>
/// 판정이 넷인 것도 같은 이유다. 마스터가 없거나 키 공간이 다른 것을 "미등록"으로 세면
/// 경보 100건이 전부 거짓이 되고, 그러면 사람이 경보를 보지 않게 된다.
/// </para>
/// </summary>
public sealed class FoundationReconciler
{
    private readonly EntityStore _entities;
    private readonly FoundationStore _foundation;

    public FoundationReconciler(EntityStore entities, FoundationStore foundation)
    {
        _entities = entities;
        _foundation = foundation;
    }

    public ReconcileReport Reconcile()
    {
        var master = _foundation.Master;
        var rows = new List<ReconcileRow>();
        var notes = new List<string>();

        foreach (var entity in _entities.All())
        {
            var (status, detail) = Judge(master, entity.Type, entity.Key);
            rows.Add(new ReconcileRow(entity.Type, entity.Key, entity.Mentions,
                entity.DistinctUsers, entity.LastSeen, status, detail));
        }

        foreach (var kind in rows.Select(r => r.Kind).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!master.Covers(kind))
                notes.Add($"{kind}: 마스터가 없다 — 무료 API나 MCP 출처를 붙이기 전까지 대사할 수 없다");
            else if (rows.Any(r => r.Kind == kind && r.Status == ReconcileStatus.Unverifiable))
                notes.Add($"{kind}: 마스터 키는 사업자번호인데 관측 키는 상호명이다 — 화면에서 사업자번호를 함께 읽어야 대사가 성립한다");
        }

        return new ReconcileReport(
            rows.Count,
            rows.Count(r => r.Status == ReconcileStatus.Matched),
            rows.Count(r => r.Status == ReconcileStatus.Unmatched),
            rows.Count(r => r.Status == ReconcileStatus.Unverifiable),
            rows.Count(r => r.Status == ReconcileStatus.NoMaster),
            rows.OrderBy(r => r.Kind, StringComparer.Ordinal)
                .ThenBy(r => r.Key, StringComparer.Ordinal).ToList(),
            notes);
    }

    private static (string Status, string? Detail) Judge(FoundationMaster master, string kind, string key)
    {
        if (!master.Covers(kind))
            return (ReconcileStatus.NoMaster, "이 종류의 기반 출처가 없다");

        if (master.Lookup(kind, key) is { } fact)
            return (ReconcileStatus.Matched, fact.Label);

        if (KeySpaceMismatch(master, kind, key))
            return (ReconcileStatus.Unverifiable, "마스터는 숫자 키(사업자번호), 관측은 문자 키");

        return (ReconcileStatus.Unmatched, null);
    }

    /// <summary>
    /// 마스터 키가 전부 숫자인데 관측 키가 아니면 대사가 성립하지 않는다.
    /// 국세청 API는 사업자번호로만 답하고 화면은 상호명을 보여 주는, 실제로 흔한 어긋남이다.
    /// </summary>
    private static bool KeySpaceMismatch(FoundationMaster master, string kind, string key)
    {
        var masterKeys = master.OfKind(kind);
        if (masterKeys.Count == 0) return false;

        return masterKeys.All(f => f.Key.All(char.IsAsciiDigit)) && !key.All(char.IsAsciiDigit);
    }
}
