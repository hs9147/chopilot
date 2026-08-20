using System.Collections.Concurrent;
using ChoPilot.Core;
using ChoPilot.Mapping;

namespace ChoPilot.Server;

public static class FeedbackDecision
{
    public const string Accept = "accept";
    public const string Correct = "correct";
    public const string Defer = "defer";
    public const string PrivacyReport = "privacy_report";

    public static bool IsValid(string value) =>
        value is Accept or Correct or Defer or PrivacyReport;
}

public static class FeedbackStatus
{
    public const string AppliedPersonal = "applied_personal";
    public const string PendingOrgReview = "pending_org_review";
    public const string Recorded = "recorded";
    public const string ApprovedOrg = "approved_org";
    public const string Rejected = "rejected";
    public const string Undone = "undone";
}

public sealed record FeedbackTarget(string Type, string ElementKey);
public sealed record FeedbackProposal(string? Concept);
public sealed record FeedbackExpected(int MappingRevision, int KnowledgeVersion);
public sealed record FeedbackCommand(
    string FeedbackId,
    string ObservationId,
    FeedbackTarget Target,
    string Decision,
    string ReasonCode,
    FeedbackProposal? Proposed,
    string RequestedScope,
    FeedbackExpected Expected);

public sealed record FeedbackRecord(
    string FeedbackId,
    string TenantId,
    string UserId,
    string ObservationId,
    FeedbackTarget Target,
    string Decision,
    string ReasonCode,
    string? ProposedConcept,
    string RequestedScope,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? UndoUntil,
    string? ReviewId,
    int MappingRevision,
    int KnowledgeVersion,
    MappingEntry? PreviousPersonal = null,
    MappingEntry? AppliedEntry = null,
    string? ReviewedBy = null,
    DateTimeOffset? ReviewedAt = null);

public sealed record ReviewField(
    string ElementKey, string? Label, string Concept, double Confidence, bool Sensitive);
public sealed record UserReviewTask(
    string ObservationId, string BusinessObject, string? ScreenTitle, string? Route,
    double Confidence, int MappingRevision, int KnowledgeVersion,
    IReadOnlyList<ReviewField> Fields, DateTimeOffset CapturedAt);

public sealed record FeedbackResult(FeedbackRecord? Record, string? Error, bool Conflict = false);

public sealed class FeedbackService
{
    private static readonly TimeSpan UndoWindow = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> Reasons = new(
        new[] { "correct", "wrong_concept", "missing_field", "wrong_context", "privacy", "other" },
        StringComparer.Ordinal);

    private readonly ObservationStore _observations;
    private readonly IMappingCache _cache;
    private readonly IKnowledgeProvider _knowledge;
    private readonly IJournal<FeedbackRecord> _journal;
    private readonly ConcurrentDictionary<string, FeedbackRecord> _records = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public FeedbackService(
        ObservationStore observations, IMappingCache cache, IKnowledgeProvider knowledge,
        IJournalFactory? journals = null)
    {
        _observations = observations;
        _cache = cache;
        _knowledge = knowledge;
        _journal = (journals ?? NullJournalFactory.Instance).Open<FeedbackRecord>("feedback");
        foreach (var record in _journal.Load()) _records[Key(record.TenantId, record.UserId, record.FeedbackId)] = record;
    }

    public IReadOnlyList<UserReviewTask> Tasks(string tenantId, string userId, int limit = 50)
    {
        var decided = _records.Values
            .Where(r => r.TenantId == tenantId && r.UserId == userId &&
                        r.Status is not FeedbackStatus.Undone and not FeedbackStatus.Rejected)
            .Select(r => r.ObservationId)
            .ToHashSet(StringComparer.Ordinal);

        return _observations.List(tenantId)
            .Where(o => o.Event.UserId == userId && !decided.Contains(o.ObservationId))
            .OrderBy(o => o.Entry.Confidence)
            .ThenByDescending(o => o.Event.CapturedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(o => new UserReviewTask(
                o.ObservationId,
                o.Entry.BusinessObject,
                o.Event.Screen.Title,
                SignatureService.NormalizeRoute(o.Event.Screen.Url),
                o.Entry.Confidence,
                o.Entry.Revision,
                _knowledge.Current.Version,
                o.Entry.Mapping.Select(m => new ReviewField(
                    m.ElementRef, Find(o.Event.Tree, m.ElementRef)?.Name,
                    m.Concept, m.Confidence, m.Sensitive)).ToList(),
                o.Event.CapturedAt))
            .ToList();
    }

    public FeedbackResult Submit(string tenantId, string userId, FeedbackCommand command)
    {
        if (!Guid.TryParse(command.FeedbackId, out _))
            return new(null, "feedback_id must be a UUID");
        if (!FeedbackDecision.IsValid(command.Decision))
            return new(null, "invalid decision");
        if (command.RequestedScope is not ("personal" or "org"))
            return new(null, "requested_scope must be personal|org");
        if (!Reasons.Contains(command.ReasonCode))
            return new(null, "invalid reason_code");
        if (command.Target.Type != "mapping")
            return new(null, "target.type must be mapping");

        lock (_gate)
        {
            var key = Key(tenantId, userId, command.FeedbackId);
            if (_records.TryGetValue(key, out var replay)) return new(replay, null);

            var observation = _observations.Get(command.ObservationId, tenantId);
            if (observation is null || observation.Event.UserId != userId)
                return new(null, "unknown observation");
            if (Find(observation.Event.Tree, command.Target.ElementKey) is null ||
                observation.Entry.Mapping.All(m => m.ElementRef != command.Target.ElementKey))
                return new(null, "unknown target element");
            if (command.Expected.MappingRevision != observation.Entry.Revision ||
                command.Expected.KnowledgeVersion != _knowledge.Current.Version)
                return new(null, "stale mapping or knowledge revision", Conflict: true);

            var now = DateTimeOffset.UtcNow;
            MappingEntry? previous = null;
            MappingEntry? applied = null;
            string status;
            DateTimeOffset? effective = null;
            DateTimeOffset? undoUntil = null;
            string? reviewId = null;

            if (command.Decision == FeedbackDecision.Correct)
            {
                var concept = _knowledge.Current.Resolve(command.Proposed?.Concept ?? "");
                if (concept is null) return new(null, "unknown proposed concept");

                if (command.RequestedScope == "org")
                {
                    status = FeedbackStatus.PendingOrgReview;
                    reviewId = Guid.NewGuid().ToString();
                }
                else
                {
                    var scope = $"personal:{userId}";
                    previous = _cache.Get(observation.Entry.Signature, scope);
                    var mapping = observation.Entry.Mapping.Select(m =>
                        m.ElementRef == command.Target.ElementKey
                            ? m with
                            {
                                Concept = concept.Name,
                                Confidence = 1,
                                Provenance = "user",
                                Sensitive = concept.Sensitive,
                            }
                            : m).ToList();
                    applied = observation.Entry with
                    {
                        Scope = scope,
                        UserId = userId,
                        Mapping = mapping,
                        Confidence = 1,
                        Status = "trusted",
                        LastInferredAt = null,
                        Revision = (previous?.Revision ?? 0) + 1,
                    };
                    _cache.Put(applied);
                    status = FeedbackStatus.AppliedPersonal;
                    effective = now;
                    undoUntil = now + UndoWindow;
                }
            }
            else
            {
                status = FeedbackStatus.Recorded;
                effective = now;
            }

            var record = new FeedbackRecord(
                command.FeedbackId, tenantId, userId, command.ObservationId, command.Target,
                command.Decision, command.ReasonCode, command.Proposed?.Concept,
                command.RequestedScope, status, now, effective, undoUntil, reviewId,
                observation.Entry.Revision, _knowledge.Current.Version, previous, applied);
            Save(record);
            return new(record, null);
        }
    }

    public FeedbackResult Review(string tenantId, string actor, string reviewId, bool approve)
    {
        lock (_gate)
        {
            var pending = _records.Values.FirstOrDefault(r =>
                r.TenantId == tenantId && r.ReviewId == reviewId &&
                r.Status == FeedbackStatus.PendingOrgReview);
            if (pending is null) return new(null, "unknown review");
            if (pending.UserId == actor) return new(null, "reviewer cannot approve own feedback");

            var observation = _observations.Get(pending.ObservationId, tenantId);
            if (observation is null) return new(null, "observation expired");

            MappingEntry? applied = null;
            var status = FeedbackStatus.Rejected;
            var now = DateTimeOffset.UtcNow;
            if (approve)
            {
                var concept = _knowledge.Current.Resolve(pending.ProposedConcept ?? "");
                if (concept is null) return new(null, "proposed concept no longer exists");
                var scope = "org:default";
                var previous = _cache.Get(observation.Entry.Signature, scope);
                applied = observation.Entry with
                {
                    Scope = scope,
                    UserId = null,
                    Mapping = observation.Entry.Mapping.Select(m =>
                        m.ElementRef == pending.Target.ElementKey
                            ? m with { Concept = concept.Name, Confidence = 1, Provenance = "reviewer", Sensitive = concept.Sensitive }
                            : m).ToList(),
                    Confidence = 1,
                    Status = "trusted",
                    LastInferredAt = null,
                    Revision = (previous?.Revision ?? 0) + 1,
                };
                _cache.Put(applied);
                status = FeedbackStatus.ApprovedOrg;
            }

            var reviewed = pending with
            {
                Status = status,
                EffectiveFrom = approve ? now : null,
                AppliedEntry = applied,
                ReviewedBy = actor,
                ReviewedAt = now,
            };
            Save(reviewed);
            return new(reviewed, null);
        }
    }

    public FeedbackResult Undo(string tenantId, string userId, string feedbackId)
    {
        lock (_gate)
        {
            var key = Key(tenantId, userId, feedbackId);
            if (!_records.TryGetValue(key, out var record) ||
                record.Status != FeedbackStatus.AppliedPersonal)
                return new(null, "feedback cannot be undone");
            if (record.UndoUntil < DateTimeOffset.UtcNow)
                return new(null, "undo window expired");
            if (record.AppliedEntry is null) return new(null, "applied mapping is missing");

            if (record.PreviousPersonal is { } previous)
                _cache.Put(previous with { Revision = record.AppliedEntry.Revision + 1 });
            else
                _cache.Remove(record.AppliedEntry.Signature, $"personal:{userId}");

            var undone = record with { Status = FeedbackStatus.Undone, EffectiveFrom = DateTimeOffset.UtcNow };
            Save(undone);
            return new(undone, null);
        }
    }

    public IReadOnlyList<FeedbackRecord> PendingReviews(string tenantId, int limit = 100) =>
        _records.Values.Where(r => r.TenantId == tenantId && r.Status == FeedbackStatus.PendingOrgReview)
            .OrderBy(r => r.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToList();

    private void Save(FeedbackRecord record)
    {
        _journal.Append(record);
        _records[Key(record.TenantId, record.UserId, record.FeedbackId)] = record;
    }

    private static string Key(string tenantId, string userId, string feedbackId) =>
        $"{tenantId}\u001f{userId}\u001f{feedbackId}";

    private static UiNode? Find(UiNode root, string key)
    {
        var stack = new Stack<UiNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Ref == key) return node;
            foreach (var child in node.Children) stack.Push(child);
        }
        return null;
    }
}
