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
