using System.Collections.Concurrent;

namespace Auricrux.Web.Services.Breakthrough;

/// <summary>
/// Process-lifetime memory for the NSF self-correction loop.
/// Atlas remains the durable store when configured; this is the source of truth
/// for the current process so GET-by-id, dashboard, and predictive control stay consistent.
/// </summary>
public sealed class BreakthroughLoopStore
{
    private readonly ConcurrentDictionary<string, ConstructionHypothesis> _hypotheses = new();
    private readonly ConcurrentDictionary<string, HypothesisComparison> _decisions = new();
    private readonly ConcurrentDictionary<string, ProvableReasoningResult> _proofs = new();
    private readonly ConcurrentBag<PhysicalVerificationResult> _verifications = [];
    private readonly ConcurrentBag<ControlRecommendation> _recommendations = [];
    private readonly ConcurrentBag<FieldLessonRecord> _fieldLessons = [];
    private readonly ConcurrentBag<PedagogyActRecord> _pedagogyActs = [];
    private readonly ConcurrentDictionary<string, PourControlRecord> _pourControls = new(StringComparer.OrdinalIgnoreCase);

    public const int HoldStripExtraDays = 3;
    public const string PourControlMutationTarget = "pour-control-process-memory";
    public const string DefaultPourProjectId = "demo-foundation-pour";

    public void CacheComparison(HypothesisComparison comparison)
    {
        _decisions[comparison.DecisionId] = comparison;
        foreach (var h in comparison.Hypotheses)
        {
            _hypotheses[h.HypothesisId] = h;
        }
    }

    public void CacheHypothesis(ConstructionHypothesis hypothesis) =>
        _hypotheses[hypothesis.HypothesisId] = hypothesis;

    public ConstructionHypothesis? FindHypothesis(string hypothesisId) =>
        _hypotheses.TryGetValue(hypothesisId, out var h) ? h : null;

    public HypothesisComparison? FindDecision(string decisionId) =>
        _decisions.TryGetValue(decisionId, out var d) ? d : null;

    public void CacheProof(string proofId, ProvableReasoningResult proof) =>
        _proofs[proofId] = proof;

    public ProvableReasoningResult? FindProof(string proofId) =>
        _proofs.TryGetValue(proofId, out var p) ? p : null;

    public void AddVerification(PhysicalVerificationResult verification) =>
        _verifications.Add(verification);

    public IReadOnlyList<PhysicalVerificationResult> ListVerifications(DateTime cutoffUtc) =>
        _verifications.Where(v => v.VerifiedAt >= cutoffUtc).OrderByDescending(v => v.VerifiedAt).ToList();

    public int DecisionCount => _decisions.Count;

    public int ProofCount => _proofs.Count;

    public void AddControlRecommendation(ControlRecommendation recommendation) =>
        _recommendations.Add(recommendation);

    public IReadOnlyList<ControlRecommendation> ListControlRecommendations(string projectId) =>
        _recommendations
            .Where(r => string.Equals(r.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToList();

    public void AddFieldLesson(FieldLessonRecord lesson) =>
        _fieldLessons.Add(lesson);

    public IReadOnlyList<FieldLessonRecord> ListFieldLessons(string? projectId, int limit = 20)
    {
        var rows = _fieldLessons.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            rows = rows.Where(l => string.Equals(l.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        }

        return rows
            .OrderByDescending(l => l.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    public void AddPedagogyAct(PedagogyActRecord act) =>
        _pedagogyActs.Add(act);

    public IReadOnlyList<PedagogyActRecord> ListPedagogyActs(string? projectId, int limit = 20)
    {
        var rows = _pedagogyActs.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            rows = rows.Where(a => string.Equals(a.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        }

        return rows
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    public PourControlRecord? GetPourControl(string? projectId)
    {
        var key = NormalizeProjectId(projectId);
        return _pourControls.TryGetValue(key, out var row) ? row : null;
    }

    public PourControlRecord EnsurePourControl(string? projectId, int baselineStripDays)
    {
        var key = NormalizeProjectId(projectId);
        var baseline = Math.Clamp(baselineStripDays, 1, 90);
        return _pourControls.AddOrUpdate(
            key,
            _ => PourControlRecord.Create(key, baseline, hold: false, actId: null),
            (_, existing) => existing);
    }

    public PourControlRecord HoldStrip(string? projectId, string actId)
    {
        var key = NormalizeProjectId(projectId);
        return _pourControls.AddOrUpdate(
            key,
            _ => PourControlRecord.Create(key, 7, hold: true, actId),
            (_, existing) => existing.WithHold(actId));
    }

    public PourControlRecord ProceedStrip(string? projectId, string actId)
    {
        var key = NormalizeProjectId(projectId);
        return _pourControls.AddOrUpdate(
            key,
            _ => PourControlRecord.Create(key, 7, hold: false, actId),
            (_, existing) => existing.WithProceed(actId));
    }

    private static string NormalizeProjectId(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? DefaultPourProjectId : projectId.Trim();
}

/// <summary>
/// Actionable control item derived from a closed breakthrough loop — not a job-cost mutation.
/// </summary>
public sealed class ControlRecommendation
{
    public required string RecommendationId { get; init; }
    public required string ProjectId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Timeframe { get; init; }
    public required string SourceDecisionId { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Job-derived field lesson from a closed loop. Process memory only unless Atlas is later configured.
/// Catalog matching is not lesson synthesis.
/// </summary>
public sealed class FieldLessonRecord
{
    public required string LessonId { get; init; }
    public required string ProjectId { get; init; }
    public required string Slice { get; init; }
    public required string ProposedAction { get; init; }
    public required string Topic { get; init; }
    public required string Lesson { get; init; }
    public required string SourceDecisionId { get; init; }
    public string DurableStore { get; init; } = "process-memory";
    public bool CatalogMatchIsNotSynthesis { get; init; } = true;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed class PedagogyActRecord
{
    public required string ActId { get; init; }
    public required string ProjectId { get; init; }
    public required string Slice { get; init; }
    public required string Action { get; init; }
    public required string DecisionId { get; init; }
    public required string VerificationId { get; init; }
    public required bool Accepted { get; init; }
    public required bool MutationApplied { get; init; }
    public required string Reason { get; init; }
    public bool CatalogMatchIsNotSynthesis { get; init; } = true;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Auricrux-owned pour stripping control. Not a PM schedule row and not a finance table.
/// A proof-gated hold-strip moves CurrentStripAtUtc; Atlas is not required.
/// </summary>
public sealed record PourControlRecord
{
    public required string ProjectId { get; init; }
    public required int BaselineStripDays { get; init; }
    public required int CurrentStripDays { get; init; }
    public required bool HoldActive { get; init; }
    public required DateTime PourAtUtc { get; init; }
    public required DateTime PlannedStripAtUtc { get; init; }
    public required DateTime CurrentStripAtUtc { get; init; }
    public string MutationTarget { get; init; } = BreakthroughLoopStore.PourControlMutationTarget;
    public bool PmOrFinanceMutated { get; init; }
    public string? LastActId { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public static PourControlRecord Create(string projectId, int baselineDays, bool hold, string? actId)
    {
        var pourAt = DateTime.UtcNow;
        var current = hold ? baselineDays + BreakthroughLoopStore.HoldStripExtraDays : baselineDays;
        return new PourControlRecord
        {
            ProjectId = projectId,
            BaselineStripDays = baselineDays,
            CurrentStripDays = current,
            HoldActive = hold,
            PourAtUtc = pourAt,
            PlannedStripAtUtc = pourAt.AddDays(baselineDays),
            CurrentStripAtUtc = pourAt.AddDays(current),
            LastActId = actId,
            PmOrFinanceMutated = false,
            UpdatedAtUtc = pourAt
        };
    }

    public PourControlRecord WithHold(string actId)
    {
        var current = BaselineStripDays + BreakthroughLoopStore.HoldStripExtraDays;
        return this with
        {
            CurrentStripDays = current,
            HoldActive = true,
            CurrentStripAtUtc = PourAtUtc.AddDays(current),
            LastActId = actId,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public PourControlRecord WithProceed(string actId) =>
        this with
        {
            CurrentStripDays = BaselineStripDays,
            HoldActive = false,
            CurrentStripAtUtc = PourAtUtc.AddDays(BaselineStripDays),
            LastActId = actId,
            UpdatedAtUtc = DateTime.UtcNow
        };
}
