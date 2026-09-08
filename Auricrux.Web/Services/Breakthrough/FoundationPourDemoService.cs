using Microsoft.Extensions.Logging;

namespace Auricrux.Web.Services.Breakthrough;

/// <summary>
/// End-to-end foundation pour demo of the Auricrux self-correction loop:
/// generate competing hypotheses → verify against simulated field measurements →
/// meta-learn → optional provable mix/strength reasoning.
/// Works without Atlas (in-memory); persists when Atlas is configured.
/// </summary>
public sealed class FoundationPourDemoService
{
    private readonly HypothesisEngine _hypothesisEngine;
    private readonly PhysicalVerificationService _verificationService;
    private readonly MetaLearningService _metaLearningService;
    private readonly ProvableReasoningService _reasoningService;
    private readonly BreakthroughLoopStore _loop;
    private readonly ILogger<FoundationPourDemoService> _logger;

    public FoundationPourDemoService(
        HypothesisEngine hypothesisEngine,
        PhysicalVerificationService verificationService,
        MetaLearningService metaLearningService,
        ProvableReasoningService reasoningService,
        ILogger<FoundationPourDemoService> logger,
        BreakthroughLoopStore? loopStore = null)
    {
        _hypothesisEngine = hypothesisEngine;
        _verificationService = verificationService;
        _metaLearningService = metaLearningService;
        _reasoningService = reasoningService;
        _loop = loopStore ?? new BreakthroughLoopStore();
        _logger = logger;
    }

    public async Task<FoundationPourDemoResult> RunAsync(
        FoundationPourDemoOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new FoundationPourDemoOptions();
        _logger.LogInformation("Running foundation pour self-correction demo (scenario={Scenario})", options.ScenarioName);

        var constraints = new Dictionary<string, object>
        {
            ["target_psi"] = options.TargetPsi,
            ["ambient_temp_f"] = options.AmbientTempF,
            ["slab_thickness_in"] = options.SlabThicknessIn,
            ["relative_humidity"] = options.RelativeHumidity,
            ["wind_speed_mph"] = options.WindSpeedMph,
            ["pile_length_ft"] = options.PileLengthFt,
            ["span_ft"] = options.SpanFt
        };

        // 1. Competing hypotheses for the pour decision
        var comparison = await _hypothesisEngine.GenerateHypothesesAsync(
            options.DecisionContext,
            options.ConstructionPhase,
            options.ProjectId,
            constraints,
            ct);

        var chosen = comparison.Hypotheses
            .FirstOrDefault(h => h.Approach == comparison.RecommendedApproach)
            ?? comparison.Hypotheses.OrderByDescending(h => h.ConfidenceScore).First();

        // 2. Simulated field outcome — deliberately off so self-correction triggers
        var actualMeasurements = BuildDivergentMeasurements(chosen.QuantitativePredictions, options);
        var actualOutcome = options.ActualOutcomeNarrative
            ?? "Field cylinders underperformed at 7 days; cure days extended; cold joint risk elevated after supply delay.";

        var verification = await _verificationService.VerifyPredictionAsync(
            chosen.HypothesisId,
            actualOutcome,
            actualMeasurements,
            evidenceUrl: options.EvidenceUrl,
            verifiedBy: options.VerifiedBy ?? "foundation-pour-demo",
            ct: ct);

        // 3. Seed additional verifications so meta-learning has enough signal (≥10)
        for (var i = 0; i < options.SeedAdditionalVerifications; i++)
        {
            var peer = comparison.Hypotheses[i % comparison.Hypotheses.Count];
            var noisy = BuildDivergentMeasurements(peer.QuantitativePredictions, options, noiseSeed: i + 1);
            await _verificationService.VerifyPredictionAsync(
                peer.HypothesisId,
                $"Seeded field verification #{i + 1} for meta-learning sample size",
                noisy,
                verifiedBy: "foundation-pour-demo-seed",
                ct: ct);
        }

        // 4. Meta-learning over recent verifications
        var meta = await _metaLearningService.AnalyzeModelErrorsAsync(
            options.ModelId,
            TimeSpan.FromDays(7),
            ct);

        // 5. Provable reasoning on mix / strength question
        var proof = await _reasoningService.GenerateProofAsync(
            options.EngineeringQuestion
                ?? $"Will a {options.TargetPsi} PSI foundation pour at {options.AmbientTempF}°F ambient reach stripping strength in {options.ExpectedStripDays} days?",
            new Dictionary<string, double>
            {
                ["target_psi"] = options.TargetPsi,
                ["ambient_temp_f"] = options.AmbientTempF,
                ["slab_thickness_in"] = options.SlabThicknessIn,
                ["required_strip_psi"] = options.TargetPsi * 0.7
            },
            ["ACI 318", "ACI 301", "ACI 306", "ACI 305R"],
            designIntent: options.DesignIntent ?? "Foundation slab pour — self-correction demo",
            ct: ct);

        PublishControlRecommendation(options, comparison, chosen, verification, proof);

        return new FoundationPourDemoResult
        {
            ScenarioName = options.ScenarioName,
            DecisionId = comparison.DecisionId,
            Hypotheses = comparison.Hypotheses,
            RecommendedApproach = comparison.RecommendedApproach,
            RecommendationReasoning = comparison.Reasoning,
            ChosenHypothesisId = chosen.HypothesisId,
            Verification = verification,
            MetaLearning = meta,
            Proof = proof,
            LoopClosed = verification.RequiresModelCorrection || meta.SystematicErrors.Count > 0,
            Summary = BuildSummary(comparison, verification, meta, proof)
        };
    }

    private static Dictionary<string, double> BuildDivergentMeasurements(
        Dictionary<string, double> predicted,
        FoundationPourDemoOptions options,
        int noiseSeed = 0)
    {
        var actual = new Dictionary<string, double>();
        foreach (var (key, value) in predicted)
        {
            // Systematic cold-weather miss: under-strength early, longer cure, higher joint risk
            actual[key] = key switch
            {
                "compressive_strength_psi_7d" => value * (0.72 + noiseSeed * 0.01),
                "compressive_strength_psi_28d" => value * (0.91 + noiseSeed * 0.005),
                "slump_inches" => Math.Max(2.0, value - 0.75),
                "cure_days_to_stripping" => value + 3 + (noiseSeed % 2),
                "cold_joint_risk_percent" => Math.Min(40, value * 1.8 + noiseSeed),
                _ => value * (0.85 + (noiseSeed % 5) * 0.02)
            };
        }

        if (options.OverrideMeasurements != null)
        {
            foreach (var (k, v) in options.OverrideMeasurements)
                actual[k] = v;
        }

        return actual;
    }

    private void PublishControlRecommendation(
        FoundationPourDemoOptions options,
        HypothesisComparison comparison,
        ConstructionHypothesis chosen,
        PhysicalVerificationResult verification,
        ProvableReasoningResult proof)
    {
        var projectId = options.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        chosen.QuantitativePredictions.TryGetValue("cure_days_to_stripping", out var stripDays);
        var title = verification.RequiresModelCorrection
            ? "Hold field action until the physics catch the cylinders"
            : $"Proceed with {comparison.RecommendedApproach}";

        _loop.AddControlRecommendation(new ControlRecommendation
        {
            RecommendationId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            Title = title,
            Description = $"{proof.Conclusion} Field accuracy {verification.AccuracyScore:P0}. {verification.CorrectionRationale}",
            Timeframe = stripDays > 0 ? $"strip ~{stripDays:0} calendar days" : "before next critical activity",
            SourceDecisionId = comparison.DecisionId
        });
    }

    public Task<FoundationPourDemoResult> RunPileAsync(
        FoundationPourDemoOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new FoundationPourDemoOptions();
        return RunAsync(options with
        {
            ScenarioName = options.ScenarioName == "Winter Foundation Pour — Self-Correction Loop"
                ? "Driven pile vs shaft vs footing — self-correction"
                : options.ScenarioName,
            DecisionContext = "Select foundation system: driven pile, drilled shaft, or improved spread footing.",
            ConstructionPhase = "foundation",
            ModelId = "auricrux-foundation-pile",
            EngineeringQuestion = options.EngineeringQuestion
                ?? $"Does a {options.PileLengthFt:0.#} ft driven pile have higher Meyerhof capacity than a 20 ft pile in the same soil?",
            DesignIntent = "Foundation system selection — NSF cognitive loop",
            ExpectedStripDays = options.ExpectedStripDays
        }, ct);
    }

    public Task<FoundationPourDemoResult> RunStructuralAsync(
        FoundationPourDemoOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new FoundationPourDemoOptions();
        return RunAsync(options with
        {
            ScenarioName = "Structural steel erection — deflection-checked self-correction",
            DecisionContext = "Steel erection sequence for a simply supported beam, check L/360 live-load deflection.",
            ConstructionPhase = "structural",
            ModelId = "auricrux-structural-steel",
            EngineeringQuestion = options.EngineeringQuestion
                ?? $"Is live-load deflection acceptable for a {options.SpanFt:0.#} ft span at 400 plf on a compact W-shape?",
            DesignIntent = "Structural steel — NSF cognitive loop"
        }, ct);
    }

    private static string BuildSummary(
        HypothesisComparison comparison,
        PhysicalVerificationResult verification,
        MetaLearningInsight meta,
        ProvableReasoningResult proof)
    {
        return
            $"Generated {comparison.Hypotheses.Count} competing pour approaches; " +
            $"recommended '{comparison.RecommendedApproach}'. " +
            $"Field verification accuracy {verification.AccuracyScore:P0}; " +
            $"correction required={verification.RequiresModelCorrection}. " +
            $"Meta-learning saw {meta.TotalPredictions} predictions, " +
            $"{meta.SystematicErrors.Count} systematic error patterns. " +
            $"Provable reasoning certainty {proof.CertaintyLevel:P0}: {proof.Conclusion}";
    }
}

public sealed record FoundationPourDemoOptions
{
    public string ScenarioName { get; init; } = "Winter Foundation Pour — Self-Correction Loop";
    public string DecisionContext { get; init; } =
        "Foundation pour for 8-inch slab-on-grade, 4000 PSI, forecast overnight low 38°F, truck spacing tight.";
    public string ConstructionPhase { get; init; } = "foundation-pour";
    public string? ProjectId { get; init; } = "demo-foundation-pour";
    public string ModelId { get; init; } = "auricrux-foundation-pour";
    public double TargetPsi { get; init; } = 4000;
    public double AmbientTempF { get; init; } = 42;
    public double SlabThicknessIn { get; init; } = 8;
    public int ExpectedStripDays { get; init; } = 7;
    public int SeedAdditionalVerifications { get; init; } = 10;
    public string? ActualOutcomeNarrative { get; init; }
    public string? EvidenceUrl { get; init; }
    public string? VerifiedBy { get; init; }
    public string? EngineeringQuestion { get; init; }
    public string? DesignIntent { get; init; }
    public Dictionary<string, double>? OverrideMeasurements { get; init; }
    public double RelativeHumidity { get; init; } = 0.55;
    public double WindSpeedMph { get; init; } = 8;
    public double PileLengthFt { get; init; } = 40;
    public double SpanFt { get; init; } = 30;
}

public sealed class FoundationPourDemoResult
{
    public required string ScenarioName { get; init; }
    public required string DecisionId { get; init; }
    public required List<ConstructionHypothesis> Hypotheses { get; init; }
    public required string RecommendedApproach { get; init; }
    public required string RecommendationReasoning { get; init; }
    public required string ChosenHypothesisId { get; init; }
    public required PhysicalVerificationResult Verification { get; init; }
    public required MetaLearningInsight MetaLearning { get; init; }
    public required ProvableReasoningResult Proof { get; init; }
    public required bool LoopClosed { get; init; }
    public required string Summary { get; init; }
}
