using Auricrux.Web.Services;
using Auricrux.Web.Services.Breakthrough;
using Auricrux.Web.Services.PhaseII;
using Microsoft.AspNetCore.Mvc;

namespace Auricrux.Web.Controllers;

/// <summary>
/// Auricrux breakthrough APIs — the self-correction loop as first-class endpoints:
/// competing hypotheses → physical verification → meta-learning → provable reasoning.
/// Every path works without Atlas (in-process cache); Atlas adds durable persistence.
/// </summary>
[ApiController]
[Route("api/breakthrough")]
public sealed class BreakthroughController(
    HypothesisEngine hypothesisEngine,
    PhysicalVerificationService verificationService,
    MetaLearningService metaLearningService,
    ProvableReasoningService reasoningService,
    FoundationPourDemoService foundationPourDemo,
    PedagogyActService pedagogyAct,
    TextbookCorpusService textbookCorpus,
    ApprenticeLessonPlanService apprenticeLessonPlans,
    CognitiveLoopService cognitiveLoop,
    ILogger<BreakthroughController> logger) : ControllerBase
{
    /// <summary>
    /// Generate competing falsifiable hypotheses for a construction decision.
    /// </summary>
    [HttpPost("hypotheses")]
    public async Task<ActionResult<HypothesisComparison>> GenerateHypotheses(
        [FromBody] GenerateHypothesesRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DecisionContext))
        {
            return BadRequest(new { error = "decisionContext is required" });
        }

        var comparison = await hypothesisEngine.GenerateHypothesesAsync(
            request.DecisionContext.Trim(),
            string.IsNullOrWhiteSpace(request.ConstructionPhase) ? "general" : request.ConstructionPhase.Trim(),
            request.ProjectId,
            request.Constraints?.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            cancellationToken);

        return Ok(comparison);
    }

    /// <summary>
    /// Retrieve a previously generated hypothesis comparison by decision id.
    /// </summary>
    [HttpGet("hypotheses/{decisionId}")]
    public async Task<ActionResult<HypothesisComparison>> GetHypotheses(
        string decisionId,
        CancellationToken cancellationToken)
    {
        var comparison = await hypothesisEngine.GetHypothesesAsync(decisionId, cancellationToken);
        if (comparison is null)
        {
            return NotFound(new { error = "Decision not found in memory or Atlas.", decisionId });
        }

        return Ok(comparison);
    }

    /// <summary>
    /// Verify a prediction against measured field reality — this is what closes the loop.
    /// </summary>
    [HttpPost("verify")]
    public async Task<ActionResult<PhysicalVerificationResult>> VerifyPrediction(
        [FromBody] VerifyPredictionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PredictionId))
        {
            return BadRequest(new { error = "predictionId is required" });
        }

        if (request.ActualMeasurements is null || request.ActualMeasurements.Count == 0)
        {
            return BadRequest(new { error = "actualMeasurements must contain at least one measurement" });
        }

        var result = await verificationService.VerifyPredictionAsync(
            request.PredictionId.Trim(),
            request.ActualOutcome?.Trim() ?? "Field outcome recorded",
            request.ActualMeasurements,
            request.EvidenceUrl,
            request.VerifiedBy,
            cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Prediction accuracy statistics over a rolling window.
    /// </summary>
    [HttpGet("accuracy")]
    public async Task<ActionResult<object>> GetAccuracy(
        [FromQuery] int periodHours = 168,
        CancellationToken cancellationToken = default)
    {
        var period = TimeSpan.FromHours(Math.Clamp(periodHours, 1, 24 * 365));
        var stats = await verificationService.GetAccuracyStatsAsync(period, cancellationToken);

        return Ok(new
        {
            periodHours = period.TotalHours,
            stats.TotalVerifications,
            stats.AverageAccuracy,
            stats.MedianAccuracy,
            stats.CorrectionRate,
            mostCommonErrors = stats.MostCommonErrors.Select(e => new { error = e.Error, count = e.Count })
        });
    }

    /// <summary>
    /// Meta-learning: detect systematic error patterns in what the model gets wrong.
    /// </summary>
    [HttpGet("meta-learning/{modelId}")]
    public async Task<ActionResult<MetaLearningInsight>> DetectSystematicErrors(
        string modelId,
        [FromQuery] int periodHours = 168,
        CancellationToken cancellationToken = default)
    {
        var period = TimeSpan.FromHours(Math.Clamp(periodHours, 1, 24 * 365));
        var insight = await metaLearningService.AnalyzeModelErrorsAsync(modelId, period, cancellationToken);
        return Ok(insight);
    }

    /// <summary>
    /// Improvement recommendations derived from recent verification history.
    /// </summary>
    [HttpGet("improvement-recommendations")]
    public async Task<ActionResult<object>> GetImprovementRecommendations(
        [FromQuery] int periodHours = 168,
        CancellationToken cancellationToken = default)
    {
        var period = TimeSpan.FromHours(Math.Clamp(periodHours, 1, 24 * 365));
        var recommendations = await metaLearningService.GetImprovementRecommendationsAsync(period, cancellationToken);
        return Ok(new { periodHours = period.TotalHours, count = recommendations.Count, recommendations });
    }

    /// <summary>
    /// Provable engineering reasoning with step-by-step math and code citations.
    /// </summary>
    [HttpPost("provable-reasoning")]
    public async Task<ActionResult<ProvableReasoningResult>> GenerateProvableReasoning(
        [FromBody] ProvableReasoningRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { error = "question is required" });
        }

        var proof = await reasoningService.GenerateProofAsync(
            request.Question.Trim(),
            request.PhysicalParameters ?? [],
            request.ApplicableCodes is { Count: > 0 } codes ? codes : ["ACI 318", "ACI 301"],
            request.DesignIntent,
            cancellationToken);

        return Ok(proof);
    }

    /// <summary>
    /// Retrieve a previously generated proof (in-memory, or Atlas when configured).
    /// </summary>
    [HttpGet("provable-reasoning/{proofId}")]
    public async Task<ActionResult<ProvableReasoningResult>> GetProvableReasoning(
        string proofId,
        CancellationToken cancellationToken)
    {
        var proof = await reasoningService.GetProofAsync(proofId, cancellationToken);
        if (proof is null)
        {
            return NotFound(new { error = "Proof not found in memory or Atlas.", proofId });
        }

        return Ok(proof);
    }

    /// <summary>
    /// Run the full foundation-pour self-correction loop end to end.
    /// </summary>
    [HttpPost("demo/foundation-pour")]
    public async Task<ActionResult<FoundationPourDemoResult>> RunFoundationPourDemo(
        [FromBody] FoundationPourDemoOptions? options,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Foundation pour breakthrough demo requested");
        var result = await foundationPourDemo.RunAsync(options, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Convenience GET for browser smoke checks (same demo, default options).
    /// </summary>
    [HttpGet("demo/foundation-pour")]
    public async Task<ActionResult<FoundationPourDemoResult>> GetFoundationPourDemo(
        CancellationToken cancellationToken)
    {
        var result = await foundationPourDemo.RunAsync(null, cancellationToken);
        return Ok(result);
    }

    /// <summary>NSF loop for pile / footing / shaft selection (same hypothesis → verify → proof cycle).</summary>
    [HttpPost("demo/driven-pile")]
    public async Task<ActionResult<FoundationPourDemoResult>> RunPileDemo(
        [FromBody] FoundationPourDemoOptions? options,
        CancellationToken cancellationToken)
        => Ok(await foundationPourDemo.RunPileAsync(options, cancellationToken));

    /// <summary>NSF loop for structural steel deflection-checked hypotheses.</summary>
    [HttpPost("demo/structural-steel")]
    public async Task<ActionResult<FoundationPourDemoResult>> RunStructuralDemo(
        [FromBody] FoundationPourDemoOptions? options,
        CancellationToken cancellationToken)
        => Ok(await foundationPourDemo.RunStructuralAsync(options, cancellationToken));

    /// <summary>
    /// Job-derived field lessons. Atlas when configured; otherwise process memory.
    /// Catalog matching is not lesson synthesis.
    /// </summary>
    [HttpGet("field-lessons")]
    public ActionResult<object> ListFieldLessons([FromQuery] string? projectId, [FromQuery] int limit = 20)
    {
        var lessons = foundationPourDemo.ListFieldLessons(projectId, limit);
        var atlas = lessons.Any(l =>
            string.Equals(l.DurableStore, BreakthroughLoopStore.AtlasDurableStore, StringComparison.OrdinalIgnoreCase));
        return Ok(new
        {
            projectId,
            durableStore = atlas
                ? BreakthroughLoopStore.AtlasDurableStore
                : BreakthroughLoopStore.ProcessMemoryDurableStore,
            atlasConfigured = pedagogyAct.AtlasConfigured,
            catalogMatchIsNotSynthesis = true,
            count = lessons.Count,
            lessons
        });
    }

    /// <summary>
    /// Proof-gated pour/steel act. Records a process-memory audit. Never mutates PM/finance.
    /// </summary>
    [HttpPost("act")]
    public async Task<ActionResult<PedagogyActResult>> Act([FromBody] PedagogyActRequest? request)
        => Ok(await pedagogyAct.Execute(request ?? new PedagogyActRequest()));

    /// <summary>
    /// Process-memory pedagogy acts. Pour-control mutations are not PM/finance mutations.
    /// </summary>
    [HttpGet("acts")]
    public ActionResult<object> ListActs([FromQuery] string? projectId, [FromQuery] int limit = 20)
    {
        var acts = pedagogyAct.List(projectId, limit);
        return Ok(new
        {
            projectId,
            durableStore = "process-memory",
            pmOrFinanceMutated = false,
            catalogMatchIsNotSynthesis = true,
            count = acts.Count,
            acts
        });
    }

    /// <summary>
    /// Auricrux pour-control stripping date. Not a PM schedule row.
    /// </summary>
    [HttpGet("pour-control")]
    public ActionResult<object> GetPourControl([FromQuery] string? projectId)
    {
        var control = pedagogyAct.GetPourControl(projectId);
        return Ok(new
        {
            projectId = string.IsNullOrWhiteSpace(projectId)
                ? BreakthroughLoopStore.DefaultPourProjectId
                : projectId,
            durableStore = pedagogyAct.AtlasConfigured
                ? BreakthroughLoopStore.AtlasDurableStore
                : BreakthroughLoopStore.ProcessMemoryDurableStore,
            mutationTarget = BreakthroughLoopStore.PourControlMutationTarget,
            pmOrFinanceMutated = false,
            control
        });
    }

    /// <summary>
    /// Auricrux steel erection-control date. Not a PM schedule row.
    /// </summary>
    [HttpGet("erection-control")]
    public ActionResult<object> GetErectionControl([FromQuery] string? projectId)
    {
        var control = pedagogyAct.GetErectionControl(projectId);
        return Ok(new
        {
            projectId = string.IsNullOrWhiteSpace(projectId)
                ? BreakthroughLoopStore.DefaultSteelProjectId
                : projectId,
            durableStore = pedagogyAct.AtlasConfigured
                ? BreakthroughLoopStore.AtlasDurableStore
                : BreakthroughLoopStore.ProcessMemoryDurableStore,
            mutationTarget = BreakthroughLoopStore.ErectionControlMutationTarget,
            pmOrFinanceMutated = false,
            control
        });
    }

    /// <summary>
    /// Atlas NSF durability counts. Empty string / not_configured means process memory only.
    /// Does not expose connection strings.
    /// </summary>
    [HttpGet("atlas-status")]
    public async Task<ActionResult<object>> GetAtlasStatus(CancellationToken cancellationToken)
    {
        var status = await pedagogyAct.GetDurabilityStatusAsync(cancellationToken);
        return Ok(new
        {
            configured = status.Configured,
            status = status.Status,
            database = "auricrux",
            hypothesisComparisons = status.HypothesisComparisons,
            fieldLessons = status.FieldLessons,
            pourControls = status.PourControls,
            erectionControls = status.ErectionControls,
            textbookChunks = status.TextbookChunks,
            apprenticeLessonPlans = status.ApprenticeLessonPlans,
            cognitiveCycles = status.CognitiveCycles,
            pmOrFinanceMutated = false
        });
    }

    /// <summary>
    /// Compose an Auricrux-owned apprentice lesson plan for a specific learner.
    /// Not catalog matching. Not unique synthesis. Does not actuate Academy catalog.
    /// </summary>
    [HttpPost("apprentice-lesson-plan")]
    public async Task<ActionResult<object>> ComposeApprenticeLessonPlan(
        [FromBody] ApprenticeLessonPlanRequestBody? body,
        CancellationToken cancellationToken)
    {
        var request = new ApprenticeLessonPlanComposer.Request(
            body?.ApprenticeId,
            body?.Role,
            body?.Slice,
            body?.ProjectId,
            body?.KnownGaps);
        var result = await apprenticeLessonPlans.ComposeAsync(request, cancellationToken);
        return Ok(new
        {
            silenced = result.Silence,
            silenceReason = result.SilenceReason,
            uniqueSynthesis = result.UniqueSynthesis,
            catalogMatching = result.CatalogMatching,
            catalogActuated = result.CatalogActuated,
            pmOrFinanceMutated = result.PmOrFinanceMutated,
            durableStore = pedagogyAct.AtlasConfigured
                ? BreakthroughLoopStore.AtlasDurableStore
                : BreakthroughLoopStore.ProcessMemoryDurableStore,
            plan = result.Plan
        });
    }

    /// <summary>
    /// List Auricrux-composed apprentice lesson plans. Not Academy catalog rows.
    /// </summary>
    [HttpGet("apprentice-lesson-plans")]
    public ActionResult<object> ListApprenticeLessonPlans(
        [FromQuery] string? apprenticeId,
        [FromQuery] int limit = 20)
    {
        var plans = apprenticeLessonPlans.List(apprenticeId, limit);
        return Ok(new
        {
            apprenticeId,
            count = plans.Count,
            uniqueSynthesis = false,
            catalogMatching = false,
            catalogActuated = false,
            pmOrFinanceMutated = false,
            durableStore = pedagogyAct.AtlasConfigured
                ? BreakthroughLoopStore.AtlasDurableStore
                : BreakthroughLoopStore.ProcessMemoryDurableStore,
            plans
        });
    }

    /// <summary>
    /// One field↔lesson↔system turn. Observes current field work, composes a
    /// job-grounded lesson, applies that lesson to Auricrux controls when
    /// human-accepted, and confirms the prior turn on the next observation.
    /// Not unique synthesis. Not catalog actuation.
    /// </summary>
    [HttpPost("cognitive-loop")]
    public async Task<ActionResult<object>> RunCognitiveLoop(
        [FromBody] CognitiveLoopRequestBody? body,
        CancellationToken cancellationToken)
    {
        var request = new CognitiveLoopComposer.Request(
            body?.ApprenticeId,
            body?.Role,
            body?.Slice,
            body?.ProjectId,
            body?.FieldActivity,
            body?.KnownGaps,
            body?.HumanAccepted ?? false,
            body?.DecisionId,
            body?.VerificationId);
        var result = await cognitiveLoop.RunAsync(request, cancellationToken);
        var cycle = result.Cycle;
        return Ok(new
        {
            silenced = result.Silence,
            silenceReason = result.SilenceReason,
            uniqueSynthesis = result.UniqueSynthesis,
            catalogMatching = result.CatalogMatching,
            catalogActuated = result.CatalogActuated,
            pmOrFinanceMutated = result.PmOrFinanceMutated,
            observe = cycle?.Observe,
            understand = cycle?.Understand,
            learn = cycle?.Learn,
            act = cycle?.Act,
            improve = cycle?.Improve,
            connect = cycle?.Connect,
            actApplied = cycle?.ActApplied ?? result.Act.Applied,
            priorTurnConfirmed = cycle?.PriorTurnConfirmed ?? false,
            holdStillInForce = cycle?.HoldStillInForce ?? false,
            cycleNumber = cycle?.CycleNumber,
            durableStore = pedagogyAct.AtlasConfigured
                ? BreakthroughLoopStore.AtlasDurableStore
                : BreakthroughLoopStore.ProcessMemoryDurableStore,
            cycle,
            plan = result.Plan,
            appliedAct = result.Act
        });
    }

    /// <summary>
    /// List field↔lesson↔system turns. Not unique synthesis.
    /// </summary>
    [HttpGet("cognitive-loop")]
    public ActionResult<object> ListCognitiveLoop(
        [FromQuery] string? apprenticeId,
        [FromQuery] string? projectId,
        [FromQuery] int limit = 20)
    {
        var cycles = cognitiveLoop.List(apprenticeId, projectId, limit);
        return Ok(new
        {
            apprenticeId,
            projectId,
            count = cycles.Count,
            uniqueSynthesis = false,
            catalogActuated = false,
            pmOrFinanceMutated = false,
            durableStore = pedagogyAct.AtlasConfigured
                ? BreakthroughLoopStore.AtlasDurableStore
                : BreakthroughLoopStore.ProcessMemoryDurableStore,
            cycles
        });
    }

    /// <summary>
    /// Claude-authored Academy textbook chunks in Atlas. Retrieval only.
    /// Does not actuate Academy catalog or CTE credentials.
    /// </summary>
    [HttpGet("textbook-corpus")]
    public async Task<ActionResult<object>> GetTextbookCorpus(
        [FromQuery] string? q,
        CancellationToken cancellationToken)
    {
        var status = await textbookCorpus.GetStatusAsync(cancellationToken);
        IReadOnlyList<TextbookCorpusHit> hits = [];
        if (!string.IsNullOrWhiteSpace(q))
            hits = await textbookCorpus.SearchAsync(q, 5, cancellationToken);
        return Ok(new
        {
            configured = status.Configured,
            status = status.Status,
            durableStore = status.Configured ? BreakthroughLoopStore.AtlasDurableStore : "not_configured",
            domain = TextbookCorpusService.Domain,
            count = status.Count,
            query = q ?? "",
            hits,
            catalogActuated = false,
            pmOrFinanceMutated = false
        });
    }
}

/// <summary>Learner-specific Auricrux lesson-plan request. Not an Academy catalog write.</summary>
public sealed class ApprenticeLessonPlanRequestBody
{
    public string? ApprenticeId { get; init; }
    public string? Role { get; init; }
    public string? Slice { get; init; }
    public string? ProjectId { get; init; }
    public List<string>? KnownGaps { get; init; }
}

/// <summary>One field↔lesson↔system cognitive-loop turn. Not unique synthesis.</summary>
public sealed class CognitiveLoopRequestBody
{
    public string? ApprenticeId { get; init; }
    public string? Role { get; init; }
    public string? Slice { get; init; }
    public string? ProjectId { get; init; }
    public string? FieldActivity { get; init; }
    public List<string>? KnownGaps { get; init; }
    public bool HumanAccepted { get; init; }
    public string? DecisionId { get; init; }
    public string? VerificationId { get; init; }
}

/// <summary>Request to generate competing hypotheses for a decision.</summary>
public sealed class GenerateHypothesesRequest
{
    /// <summary>What decision is being made, in field language.</summary>
    public string DecisionContext { get; init; } = "";

    /// <summary>Construction phase, e.g. foundation-pour, structural, schedule.</summary>
    public string ConstructionPhase { get; init; } = "general";

    /// <summary>Optional project association.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Known numeric constraints, e.g. target_psi, ambient_temp_f, slab_thickness_in.</summary>
    public Dictionary<string, double>? Constraints { get; init; }
}

/// <summary>Request to verify a prediction against measured field results.</summary>
public sealed class VerifyPredictionRequest
{
    /// <summary>Hypothesis id returned when the prediction was generated.</summary>
    public string PredictionId { get; init; } = "";

    /// <summary>Narrative of what actually happened in the field.</summary>
    public string? ActualOutcome { get; init; }

    /// <summary>Measured values keyed by the same names used in the prediction.</summary>
    public Dictionary<string, double>? ActualMeasurements { get; init; }

    /// <summary>Optional link to photos, cylinder breaks, or reports.</summary>
    public string? EvidenceUrl { get; init; }

    /// <summary>Who recorded the verification.</summary>
    public string? VerifiedBy { get; init; }
}

/// <summary>Request for a provable engineering answer.</summary>
public sealed class ProvableReasoningRequest
{
    /// <summary>Engineering question to prove.</summary>
    public string Question { get; init; } = "";

    /// <summary>Numeric inputs the proof should use.</summary>
    public Dictionary<string, double>? PhysicalParameters { get; init; }

    /// <summary>Codes and standards to cite.</summary>
    public List<string>? ApplicableCodes { get; init; }

    /// <summary>Optional design intent for context.</summary>
    public string? DesignIntent { get; init; }
}
