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
