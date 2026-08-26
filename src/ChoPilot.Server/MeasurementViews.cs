using ChoPilot.Core;

namespace ChoPilot.Server;

/// <summary>측정 UI가 그리는 스냅샷 1건 요약 (PHASE0-MEASUREMENT §3).</summary>
public sealed record ObservationSummary(
    string ObservationId,
    DateTimeOffset CapturedAt,
    string? Url,
    string Route,
    string? Title,
    string Signature,
    string BusinessObject,
    double Confidence,
    string Status,
    string Provenance,
    bool CacheHit,

    /// <summary>
    /// 이 관측이 어디서 답을 얻었는지 — <c>trusted_cache</c> | <c>deferred_cache</c> | <c>ai</c>.
    /// <see cref="CacheHit"/>는 두 갈래라 <b>재추론 보류(θ 미만 캐시 재사용)를 AI 호출과 구분하지 못한다</b>.
    /// 그 구분이 없으면 화면이 "AI를 매번 부른다"고 잘못 말하게 된다.
    /// </summary>
    string Source,

    RecordHint? RecordHint,
    int NodeCount,
    int NamedCount,
    int ValuedCount,
    int MaskedCount,
    int ResidualPiiCount);

/// <summary>인벤토리 1행 (PHASE0-KIT §2.1 요소 인벤토리에 대응).</summary>
public sealed record InventoryNode(
    string Ref,
    int Depth,
    string Role,
    string? Name,
    string? Value,
    string? AutomationId,
    bool HasValue,
    bool Masked,
    bool ResidualPii);

public sealed record ObservationDetail(
    ObservationSummary Summary,
    List<InventoryNode> Nodes,
    List<FieldMapping> Mapping);

/// <summary>화면에 실제로 보였던 필드 1개 — AI 판단을 대조할 원본.</summary>
public sealed record ScreenField(string Ref, string? Label, string? Value, bool Masked);

/// <summary>
/// 서명 1개가 가리키는 화면의 필드 목록.
///
/// <para>
/// 검수 큐는 <c>n2 → Vendor</c> 처럼 <b>ref와 정규 개념만</b> 들고 있다. 사람이 그 판단이 맞는지
/// 보려면 <c>n2</c>가 화면의 어느 칸이었는지를 알아야 하는데, 매핑에는 그 정보가 없다 —
/// 화면 쪽에만 있다. 그래서 같은 서명의 최근 관측에서 라벨과 값을 끌어와 붙여 준다.
/// </para>
/// <para>
/// 값은 마스킹된 트리에서 온다. <see cref="ScreenField.Masked"/>가 붙은 칸은 원값이 아니라
/// 가려진 표시다 — 검수 화면이 마스킹 방어선을 우회하는 창구가 되면 안 된다.
/// </para>
/// </summary>
public sealed record ScreenFields(
    string Signature, string Route, string? Title, DateTimeOffset CapturedAt, List<ScreenField> Fields);

/// <summary>한 화면(route)이 몇 개의 서명으로 갈렸는지 — 캐시 적중률 미달의 1차 원인 진단.</summary>
public sealed record SignatureGroup(string Signature, int ObservationCount, List<string> ObservationIds);

public sealed record RouteDiagnosis(
    string Route,
    int ObservationCount,
    int SignatureCount,
    bool Split,                 // 서명이 2개 이상 = 같은 화면이 갈렸다
    List<SignatureGroup> Signatures);

/// <summary>저장된 관측을 측정 UI용 뷰로 투영한다. 순수 변환 — 상태를 갖지 않는다.</summary>
public static class MeasurementViews
{
    public static ObservationSummary Summarize(StoredObservation stored, PrivacyGate gate, bool cacheHit)
    {
        var evt = stored.Event;
        var nodes = Flatten(evt.Tree);
        var masked = evt.Privacy.MaskedRefs.ToHashSet();
        var residual = gate.ScanResidual(evt.Screen, evt.Tree);

        return new ObservationSummary(
            ObservationId: stored.ObservationId,
            CapturedAt: evt.CapturedAt,
            Url: evt.Screen.Url,
            Route: SignatureService.NormalizeRoute(evt.Screen.Url),
            Title: evt.Screen.Title,
            Signature: SignatureService.Compute(evt.Screen, evt.Tree),
            BusinessObject: stored.Entry.BusinessObject,
            Confidence: stored.Entry.Confidence,
            Status: stored.Entry.Status,
            Provenance: stored.BusinessObject.Provenance,
            CacheHit: cacheHit,
            Source: stored.Source,
            RecordHint: evt.Screen.RecordHint,
            NodeCount: nodes.Count,
            NamedCount: nodes.Count(n => !string.IsNullOrWhiteSpace(n.Node.Name)),
            ValuedCount: nodes.Count(n => !string.IsNullOrEmpty(n.Node.Value)),
            MaskedCount: masked.Count,
            ResidualPiiCount: residual.Count);
    }

    public static ObservationDetail Detail(StoredObservation stored, PrivacyGate gate, bool cacheHit)
    {
        var evt = stored.Event;
        var masked = evt.Privacy.MaskedRefs.ToHashSet();
        var residual = gate.ScanResidual(evt.Screen, evt.Tree).ToHashSet();

        var nodes = Flatten(evt.Tree)
            .Select(n => new InventoryNode(
                Ref: n.Node.Ref,
                Depth: n.Depth,
                Role: n.Node.Role,
                Name: n.Node.Name,
                Value: n.Node.Value,
                AutomationId: n.Node.AutomationId,
                HasValue: !string.IsNullOrEmpty(n.Node.Value),
                Masked: masked.Contains(n.Node.Ref),
                ResidualPii: residual.Contains(n.Node.Ref)))
            .ToList();

        return new ObservationDetail(Summarize(stored, gate, cacheHit), nodes, stored.Entry.Mapping);
    }

    /// <summary>
    /// 서명 → 그 화면의 필드 목록. 같은 서명의 관측이 여럿이면 <b>가장 최근 것</b>을 쓴다 —
    /// 서명이 같다는 건 구조가 같다는 뜻이므로 라벨은 같고, 값만 최근 것이 된다.
    /// </summary>
    public static Dictionary<string, ScreenFields> ScreensBySignature(
        IEnumerable<StoredObservation> observations)
    {
        var screens = new Dictionary<string, ScreenFields>(StringComparer.Ordinal);

        foreach (var stored in observations.OrderBy(s => s.Seq))
        {
            var evt = stored.Event;
            var signature = SignatureService.Compute(evt.Screen, evt.Tree);
            var masked = evt.Privacy.MaskedRefs.ToHashSet();

            // 컨테이너는 판단 대상이 아니다 — 라벨도 값도 없는 노드를 실으면 대조표가 껍데기로 길어진다.
            var fields = Flatten(evt.Tree)
                .Select(n => n.Node)
                .Where(n => !string.IsNullOrWhiteSpace(n.Name) || !string.IsNullOrEmpty(n.Value))
                .Select(n => new ScreenField(n.Ref, n.Name, n.Value, masked.Contains(n.Ref)))
                .ToList();

            screens[signature] = new ScreenFields(
                signature,
                SignatureService.NormalizeRoute(evt.Screen.Url),
                evt.Screen.Title,
                evt.CapturedAt,
                fields);
        }

        return screens;
    }

    /// <summary>route별로 서명을 묶는다. <c>Split=true</c>면 같은 화면이 여러 서명으로 갈린 것.</summary>
    public static List<RouteDiagnosis> DiagnoseRoutes(IEnumerable<StoredObservation> observations)
    {
        return observations
            .Select(s => (Stored: s, Route: SignatureService.NormalizeRoute(s.Event.Screen.Url),
                          Signature: SignatureService.Compute(s.Event.Screen, s.Event.Tree)))
            .GroupBy(x => x.Route)
            .Select(routeGroup =>
            {
                var signatures = routeGroup
                    .GroupBy(x => x.Signature)
                    .Select(sigGroup => new SignatureGroup(
                        sigGroup.Key,
                        sigGroup.Count(),
                        sigGroup.Select(x => x.Stored.ObservationId).ToList()))
                    .OrderByDescending(g => g.ObservationCount)
                    .ToList();

                return new RouteDiagnosis(
                    Route: routeGroup.Key,
                    ObservationCount: routeGroup.Count(),
                    SignatureCount: signatures.Count,
                    Split: signatures.Count > 1,
                    Signatures: signatures);
            })
            .OrderByDescending(r => r.SignatureCount)
            .ThenBy(r => r.Route)
            .ToList();
    }

    private static List<(UiNode Node, int Depth)> Flatten(UiNode root)
    {
        var result = new List<(UiNode, int)>();
        Walk(root, 0);
        return result;

        void Walk(UiNode node, int depth)
        {
            result.Add((node, depth));
            foreach (var child in node.Children) Walk(child, depth + 1);
        }
    }
}
