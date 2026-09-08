using Auricrux.Web.Services;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Auricrux.Web.Services.Breakthrough;

/// <summary>
/// PHASE 4 BREAKTHROUGH: Physical Verification Service
/// 
/// Compares Auricrux's PREDICTIONS against ACTUAL PHYSICAL OUTCOMES.
/// 
/// The Innovation:
/// 1. Auricrux makes falsifiable, quantitative predictions (via HypothesisEngine)
/// 2. Field teams capture actual measurements/outcomes
/// 3. This service calculates prediction accuracy
/// 4. Identifies systematic errors in Auricrux's reasoning
/// 5. Feeds corrections back to the model
/// 
/// This creates SELF-CORRECTING INTELLIGENCE:
/// - Auricrux learns from being WRONG, not just from being right
/// - Prediction accuracy improves over time
/// - Construction-specific errors are caught and fixed
/// 
/// INNOVATION: No other construction AI verifies its predictions against physical reality.
/// This is the "provably gets better" feature that makes Auricrux patent-worthy.
/// </summary>
public sealed class PhysicalVerificationService
{
    private readonly AtlasService _atlas;
    private readonly HypothesisEngine _hypothesisEngine;
    private readonly BreakthroughLoopStore _loop;
    private readonly ILogger<PhysicalVerificationService> _logger;

    public PhysicalVerificationService(
        AtlasService atlas,
        HypothesisEngine hypothesisEngine,
        ILogger<PhysicalVerificationService> logger,
        BreakthroughLoopStore? loopStore = null)
    {
        _atlas = atlas;
        _hypothesisEngine = hypothesisEngine;
        _loop = loopStore ?? new BreakthroughLoopStore();
        _logger = logger;
    }

    /// <summary>
    /// Verify a prediction against actual physical outcome
    /// </summary>
    public async Task<PhysicalVerificationResult> VerifyPredictionAsync(
        string predictionId,
        string actualOutcome,
        Dictionary<string, double> actualMeasurements,
        string? evidenceUrl = null,
        string? verifiedBy = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Verifying prediction {PredictionId} against actual outcome", predictionId);

        // Retrieve the original prediction/hypothesis
        var hypothesis = await RetrieveHypothesisAsync(predictionId, ct);
        if (hypothesis == null)
        {
            throw new InvalidOperationException($"Prediction {predictionId} not found for verification");
        }

        // Calculate accuracy for each quantitative prediction
        var variances = CalculateMeasurementVariances(
            hypothesis.QuantitativePredictions,
            actualMeasurements);

        // Identify systematic errors
        var errors = IdentifyPredictionErrors(
            hypothesis.PredictedOutcome,
            actualOutcome,
            variances);

        // Calculate overall accuracy score
        var accuracyScore = CalculateOverallAccuracy(variances);

        // Determine if model correction is needed
        var requiresCorrection = DetermineIfCorrectionNeeded(accuracyScore, errors);
        var correctionRationale = BuildCorrectionRationale(errors, variances);

        var verificationId = Guid.NewGuid().ToString();
        var result = new PhysicalVerificationResult
        {
            VerificationId = verificationId,
            PredictionId = predictionId,
            AccuracyScore = accuracyScore,
            StatedConfidence = hypothesis.ConfidenceScore,
            MeasurementVariances = variances,
            IdentifiedErrors = errors,
            RequiresModelCorrection = requiresCorrection,
            CorrectionRationale = correctionRationale,
            ActualOutcome = actualOutcome,
            EvidenceUrl = evidenceUrl,
            VerifiedBy = verifiedBy,
            VerifiedAt = DateTime.UtcNow
        };

        // Persist verification
        _loop.AddVerification(result);
        if (_atlas.IsConfigured)
        {
            await PersistVerificationAsync(result, hypothesis, ct);
        }

        // If correction needed, trigger meta-learning
        if (requiresCorrection)
        {
            _logger.LogWarning(
                "Prediction {PredictionId} accuracy {Accuracy:F2} requires model correction: {Rationale}",
                predictionId, accuracyScore, correctionRationale);
        }

        return result;
    }

    /// <summary>
    /// Get verification history for analysis
    /// </summary>
    public async Task<List<PhysicalVerificationResult>> GetVerificationHistoryAsync(
        TimeSpan period,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.Subtract(period);
        var fromMemory = _loop.ListVerifications(cutoff).ToList();

        if (!_atlas.IsConfigured) return fromMemory;

        try
        {
            var filter = Builders<BsonDocument>.Filter.Gte("verified_at", cutoff);

            var docs = await _atlas.Database
                .GetCollection<BsonDocument>("physical_verifications")
                .Find(filter)
                .SortByDescending(d => d["verified_at"])
                .ToListAsync(ct);

            var fromAtlas = docs.Select(MapVerificationFromBson).ToList();
            // Prefer Atlas when present; fall back to memory for any not yet persisted
            var atlasIds = fromAtlas.Select(v => v.VerificationId).ToHashSet();
            return fromAtlas
                .Concat(fromMemory.Where(v => !atlasIds.Contains(v.VerificationId)))
                .OrderByDescending(v => v.VerifiedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading verification history; returning in-memory results");
            return fromMemory;
        }
    }

    /// <summary>
    /// Get prediction accuracy statistics
    /// </summary>
    public async Task<PredictionAccuracyStats> GetAccuracyStatsAsync(
        TimeSpan period,
        CancellationToken ct = default)
    {
        var verifications = await GetVerificationHistoryAsync(period, ct);

        if (verifications.Count == 0)
        {
            return new PredictionAccuracyStats
            {
                Period = period,
                TotalVerifications = 0,
                AverageAccuracy = 0,
                MedianAccuracy = 0,
                CorrectionRate = 0
            };
        }

        var accuracies = verifications.Select(v => v.AccuracyScore).OrderBy(a => a).ToList();

        return new PredictionAccuracyStats
        {
            Period = period,
            TotalVerifications = verifications.Count,
            AverageAccuracy = accuracies.Average(),
            MedianAccuracy = accuracies[accuracies.Count / 2],
            CorrectionRate = (double)verifications.Count(v => v.RequiresModelCorrection) / verifications.Count,
            MostCommonErrors = verifications
                .SelectMany(v => v.IdentifiedErrors)
                .GroupBy(e => e)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => (g.Key, g.Count()))
                .ToList()
        };
    }

    // ── Private Implementation ──────────────────────────────────────────────────

    private async Task<ConstructionHypothesis?> RetrieveHypothesisAsync(string hypothesisId, CancellationToken ct)
    {
        // Memory first (works without Atlas for demo / sovereign paths)
        var fromEngine = await _hypothesisEngine.FindHypothesisByIdAsync(hypothesisId, ct);
        if (fromEngine != null) return fromEngine;

        if (!_atlas.IsConfigured) return null;

        try
        {
            // Search in hypothesis_comparisons for this hypothesis ID
            var filter = Builders<BsonDocument>.Filter.ElemMatch<BsonValue>(
                "hypotheses",
                new BsonDocument { ["hypothesis_id"] = hypothesisId });

            var doc = await _atlas.Database
                .GetCollection<BsonDocument>("hypothesis_comparisons")
                .Find(filter)
                .FirstOrDefaultAsync(ct);

            if (doc == null) return null;

            // Extract the specific hypothesis
            var hypothesesArray = doc["hypotheses"].AsBsonArray;
            var hypothesisDoc = hypothesesArray
                .Select(h => h.AsBsonDocument)
                .FirstOrDefault(h => h["hypothesis_id"].AsString == hypothesisId);

            if (hypothesisDoc == null) return null;

            return new ConstructionHypothesis
            {
                HypothesisId = hypothesisDoc["hypothesis_id"].AsString,
                Approach = hypothesisDoc["approach"].AsString,
                PredictedOutcome = hypothesisDoc["predicted_outcome"].AsString,
                Assumptions = hypothesisDoc["assumptions"].AsBsonArray.Select(a => a.AsString).ToList(),
                QuantitativePredictions = hypothesisDoc["quantitative_predictions"].AsBsonDocument.ToDictionary(
                    e => e.Name,
                    e => e.Value.ToDouble()),
                ConfidenceScore = hypothesisDoc["confidence_score"].ToDouble(),
                RiskFactors = hypothesisDoc["risk_factors"].AsBsonArray.Select(r => r.AsString).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving hypothesis {HypothesisId}", hypothesisId);
            return null;
        }
    }

    private Dictionary<string, MeasurementVariance> CalculateMeasurementVariances(
        Dictionary<string, double> predicted,
        Dictionary<string, double> actual)
    {
        var variances = new Dictionary<string, MeasurementVariance>();

        foreach (var kvp in predicted)
        {
            if (!actual.TryGetValue(kvp.Key, out var actualValue))
            {
                // Measurement was predicted but not captured
                variances[kvp.Key] = new MeasurementVariance
                {
                    Measurement = kvp.Key,
                    Predicted = kvp.Value,
                    Actual = null,
                    VariancePercent = null,
                    WithinTolerance = false,
                    Note = "Actual measurement not captured"
                };
                continue;
            }

            var predictedVal = kvp.Value;
            var variancePercent = predictedVal == 0
                ? 100 // If predicted 0 and actual != 0, that's a major error
                : Math.Abs((actualValue - predictedVal) / predictedVal) * 100;

            // Construction tolerance: ±15% is typical for estimates
            var withinTolerance = variancePercent <= 15;

            variances[kvp.Key] = new MeasurementVariance
            {
                Measurement = kvp.Key,
                Predicted = predictedVal,
                Actual = actualValue,
                VariancePercent = variancePercent,
                WithinTolerance = withinTolerance,
                Note = withinTolerance ? "Within tolerance" : $"Variance {variancePercent:F1}% exceeds ±15% tolerance"
            };
        }

        // Check for measurements that were captured but not predicted
        foreach (var kvp in actual)
        {
            if (!predicted.ContainsKey(kvp.Key))
            {
                variances[kvp.Key] = new MeasurementVariance
                {
                    Measurement = kvp.Key,
                    Predicted = null,
                    Actual = kvp.Value,
                    VariancePercent = null,
                    WithinTolerance = false,
                    Note = "Measurement captured but not predicted (missing prediction)"
                };
            }
        }

        return variances;
    }

    private List<string> IdentifyPredictionErrors(
        string predictedOutcome,
        string actualOutcome,
        Dictionary<string, MeasurementVariance> variances)
    {
        var errors = new List<string>();

        // Check if outcome narrative matches
        if (!OutcomesMatch(predictedOutcome, actualOutcome))
        {
            errors.Add($"Qualitative outcome mismatch: predicted '{predictedOutcome}' but actual '{actualOutcome}'");
        }

        // Check for systematic measurement errors
        var majorVariances = variances.Values
            .Where(v => v.VariancePercent.HasValue && v.VariancePercent.Value > 25)
            .ToList();

        foreach (var variance in majorVariances)
        {
            errors.Add($"Major variance in {variance.Measurement}: {variance.VariancePercent:F1}% error");
        }

        // Check for missed predictions
        var missedMeasurements = variances.Values
            .Where(v => v.Predicted == null)
            .ToList();

        if (missedMeasurements.Count > 0)
        {
            errors.Add($"Failed to predict {missedMeasurements.Count} measured parameters: {string.Join(", ", missedMeasurements.Select(m => m.Measurement))}");
        }

        return errors;
    }

    private bool OutcomesMatch(string predicted, string actual)
    {
        // Simple semantic match - in production would use NLP
        var predictedLower = predicted.ToLowerInvariant();
        var actualLower = actual.ToLowerInvariant();

        // Check for negative/positive alignment
        var predictedPositive = predictedLower.Contains("success") || predictedLower.Contains("effective") || predictedLower.Contains("high");
        var actualPositive = actualLower.Contains("success") || actualLower.Contains("effective") || actualLower.Contains("high");

        return predictedPositive == actualPositive;
    }

    private double CalculateOverallAccuracy(Dictionary<string, MeasurementVariance> variances)
    {
        var validVariances = variances.Values
            .Where(v => v.VariancePercent.HasValue)
            .ToList();

        if (validVariances.Count == 0) return 0.0;

        // Accuracy = 100% - average variance %
        var averageVariance = validVariances.Average(v => v.VariancePercent!.Value);
        var accuracy = Math.Max(0, 100 - averageVariance) / 100.0;

        return Math.Round(accuracy, 3);
    }

    private bool DetermineIfCorrectionNeeded(double accuracyScore, List<string> errors)
    {
        // Correction needed if accuracy < 75% OR if there are systematic errors
        return accuracyScore < 0.75 || errors.Count >= 2;
    }

    private string BuildCorrectionRationale(List<string> errors, Dictionary<string, MeasurementVariance> variances)
    {
        if (errors.Count == 0) return "Prediction within acceptable tolerance";

        var majorErrors = errors.Take(3);
        var worstVariance = variances.Values
            .Where(v => v.VariancePercent.HasValue)
            .OrderByDescending(v => v.VariancePercent!.Value)
            .FirstOrDefault();

        var rationale = $"Model correction recommended: {string.Join("; ", majorErrors)}.";

        if (worstVariance != null)
        {
            rationale += $" Worst variance: {worstVariance.Measurement} at {worstVariance.VariancePercent:F1}%.";
        }

        return rationale;
    }

    private async Task PersistVerificationAsync(
        PhysicalVerificationResult result,
        ConstructionHypothesis hypothesis,
        CancellationToken ct)
    {
        try
        {
            var doc = new BsonDocument
            {
                ["verification_id"] = result.VerificationId,
                ["prediction_id"] = result.PredictionId,
                ["hypothesis_approach"] = hypothesis.Approach,
                ["predicted_outcome"] = hypothesis.PredictedOutcome,
                ["actual_outcome"] = result.ActualOutcome,
                ["accuracy_score"] = result.AccuracyScore,
                ["stated_confidence"] = result.StatedConfidence,
                ["measurement_variances"] = new BsonDocument(result.MeasurementVariances.Select(kvp => new BsonElement(
                    kvp.Key,
                    new BsonDocument
                    {
                        ["predicted"] = kvp.Value.Predicted.HasValue ? (BsonValue)kvp.Value.Predicted.Value : BsonNull.Value,
                        ["actual"] = kvp.Value.Actual.HasValue ? (BsonValue)kvp.Value.Actual.Value : BsonNull.Value,
                        ["variance_percent"] = kvp.Value.VariancePercent.HasValue ? (BsonValue)kvp.Value.VariancePercent.Value : BsonNull.Value,
                        ["within_tolerance"] = kvp.Value.WithinTolerance,
                        ["note"] = kvp.Value.Note
                    }))),
                ["identified_errors"] = new BsonArray(result.IdentifiedErrors),
                ["requires_correction"] = result.RequiresModelCorrection,
                ["correction_rationale"] = result.CorrectionRationale,
                ["evidence_url"] = result.EvidenceUrl ?? "",
                ["verified_by"] = result.VerifiedBy ?? "",
                ["verified_at"] = result.VerifiedAt
            };

            await _atlas.Database
                .GetCollection<BsonDocument>("physical_verifications")
                .InsertOneAsync(doc, cancellationToken: ct);

            // Mark the original hypothesis as verified
            await _atlas.Database
                .GetCollection<BsonDocument>("hypothesis_comparisons")
                .UpdateOneAsync(
                    Builders<BsonDocument>.Filter.ElemMatch<BsonValue>(
                        "hypotheses",
                        new BsonDocument { ["hypothesis_id"] = result.PredictionId }),
                    Builders<BsonDocument>.Update
                        .Set("verified", true)
                        .Set("verification_id", result.VerificationId)
                        .Set("verification_accuracy", result.AccuracyScore),
                    cancellationToken: ct);

            _logger.LogInformation("Persisted verification {VerificationId} with accuracy {Accuracy:F2}",
                result.VerificationId, result.AccuracyScore);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist physical verification");
        }
    }

    private PhysicalVerificationResult MapVerificationFromBson(BsonDocument doc)
    {
        var variances = doc["measurement_variances"].AsBsonDocument.ToDictionary(
            e => e.Name,
            e => new MeasurementVariance
            {
                Measurement = e.Name,
                Predicted = e.Value["predicted"] == BsonNull.Value ? null : e.Value["predicted"].ToDouble(),
                Actual = e.Value["actual"] == BsonNull.Value ? null : e.Value["actual"].ToDouble(),
                VariancePercent = e.Value["variance_percent"] == BsonNull.Value ? null : e.Value["variance_percent"].ToDouble(),
                WithinTolerance = e.Value["within_tolerance"].AsBoolean,
                Note = e.Value["note"].AsString
            });

        return new PhysicalVerificationResult
        {
            VerificationId = doc["verification_id"].AsString,
            PredictionId = doc["prediction_id"].AsString,
            AccuracyScore = doc["accuracy_score"].ToDouble(),
            StatedConfidence = doc.GetValue("stated_confidence", 0.0).ToDouble(),
            MeasurementVariances = variances,
            IdentifiedErrors = doc["identified_errors"].AsBsonArray.Select(e => e.AsString).ToList(),
            RequiresModelCorrection = doc["requires_correction"].AsBoolean,
            CorrectionRationale = doc["correction_rationale"].AsString,
            ActualOutcome = doc["actual_outcome"].AsString,
            EvidenceUrl = doc.GetValue("evidence_url", "").AsString,
            VerifiedBy = doc.GetValue("verified_by", "").AsString,
            VerifiedAt = doc["verified_at"].ToUniversalTime()
        };
    }
}

// ── Models ──────────────────────────────────────────────────────────────────────

public sealed class PhysicalVerificationResult
{
    public required string VerificationId { get; init; }
    public required string PredictionId { get; init; }
    public required double AccuracyScore { get; init; }
    /// <summary>Hypothesis confidence at prediction time. 0 when unknown (legacy rows).</summary>
    public double StatedConfidence { get; init; }
    public required Dictionary<string, MeasurementVariance> MeasurementVariances { get; init; }
    public required List<string> IdentifiedErrors { get; init; }
    public required bool RequiresModelCorrection { get; init; }
    public required string CorrectionRationale { get; init; }
    public required string ActualOutcome { get; init; }
    public string? EvidenceUrl { get; init; }
    public string? VerifiedBy { get; init; }
    public DateTime VerifiedAt { get; init; }
}

public sealed class MeasurementVariance
{
    public required string Measurement { get; init; }
    public double? Predicted { get; init; }
    public double? Actual { get; init; }
    public double? VariancePercent { get; init; }
    public required bool WithinTolerance { get; init; }
    public required string Note { get; init; }
}

public sealed class PredictionAccuracyStats
{
    public TimeSpan Period { get; init; }
    public int TotalVerifications { get; init; }
    public double AverageAccuracy { get; init; }
    public double MedianAccuracy { get; init; }
    public double CorrectionRate { get; init; }
    public List<(string Error, int Count)> MostCommonErrors { get; init; } = [];
}
