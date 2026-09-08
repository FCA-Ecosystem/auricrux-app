using Auricrux.Web.Services;
using Auricrux.Web.Services.Breakthrough;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Auricrux.Tests;

/// <summary>
/// Proves auricrux-app hosts the foundation-pour self-correction loop without Atlas.
/// </summary>
public sealed class FoundationPourSelfCorrectionTests
{
    private static (FoundationPourDemoService Demo, BreakthroughLoopStore Loop) CreateDemoService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var atlas = new AtlasService(config, NullLogger<AtlasService>.Instance);
        var loop = new BreakthroughLoopStore();
        var hypotheses = new HypothesisEngine(atlas, NullLogger<HypothesisEngine>.Instance, loop);
        var verification = new PhysicalVerificationService(atlas, hypotheses, NullLogger<PhysicalVerificationService>.Instance, loop);
        var meta = new MetaLearningService(atlas, verification, NullLogger<MetaLearningService>.Instance);
        var reasoning = new ProvableReasoningService(atlas, NullLogger<ProvableReasoningService>.Instance, loop);
        var demo = new FoundationPourDemoService(hypotheses, verification, meta, reasoning, NullLogger<FoundationPourDemoService>.Instance, loop);
        return (demo, loop);
    }

    [Fact]
    public async Task FoundationPourDemo_ClosesSelfCorrectionLoop_WithoutAtlas()
    {
        var (demo, loop) = CreateDemoService();
        var projectId = $"test-pour-{Guid.NewGuid():N}";

        var result = await demo.RunAsync(new FoundationPourDemoOptions
        {
            SeedAdditionalVerifications = 10,
            ProjectId = projectId
        });

        Assert.Equal(4, result.Hypotheses.Count);
        Assert.Contains(result.Hypotheses, h => h.Approach.Contains("Hot-Weather", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(result.RecommendedApproach));
        Assert.False(string.IsNullOrWhiteSpace(result.ChosenHypothesisId));
        Assert.True(result.Verification.AccuracyScore < 1.0);
        Assert.True(result.Verification.RequiresModelCorrection);
        Assert.True(result.MetaLearning.TotalPredictions >= 10);
        Assert.False(string.IsNullOrWhiteSpace(result.Proof.Conclusion));
        Assert.True(result.LoopClosed);
        Assert.Contains("correction required=True", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Hypotheses[0].QuantitativePredictions);
        Assert.NotEmpty(result.Hypotheses[0].RiskFactors);
        Assert.False(string.IsNullOrWhiteSpace(result.Verification.CorrectionRationale));
        Assert.NotEmpty(result.Proof.ProofSteps);
        Assert.NotEmpty(result.Proof.CitedStandards);
        Assert.NotEmpty(loop.ListControlRecommendations(projectId));
        Assert.False(result.PedagogySilence);
        Assert.Equal("hold-strip", result.PedagogyProposedAction);
        Assert.Contains("not catalog matching", result.PedagogyLesson, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.PedagogyRecorded);
        Assert.False(string.IsNullOrWhiteSpace(result.PedagogyLessonId));
        Assert.NotEmpty(loop.ListFieldLessons(projectId));
        Assert.False(result.PedagogyPriorLessonConfirmed);

        var confirmed = await demo.RunAsync(new FoundationPourDemoOptions
        {
            SeedAdditionalVerifications = 10,
            ProjectId = projectId
        });
        Assert.True(confirmed.PedagogyPriorLessonConfirmed);
        Assert.Equal("hold-strip", confirmed.PedagogyProposedAction);
        Assert.Contains("Prior job lesson confirmed", confirmed.PedagogyLessonTopic, StringComparison.Ordinal);
        Assert.Contains("Still not unique synthesis", confirmed.PedagogyLesson, StringComparison.Ordinal);
        Assert.NotNull(result.PourControl);
        Assert.False(result.PourControl!.HoldActive);
        Assert.Equal(7, result.PourControl.CurrentStripDays);

        var held = loop.HoldStrip(projectId, "act-hold");
        Assert.True(held.HoldActive);
        Assert.Equal(7 + BreakthroughLoopStore.HoldStripExtraDays, held.CurrentStripDays);
        Assert.True(held.CurrentStripAtUtc > held.PlannedStripAtUtc);
        Assert.False(held.PmOrFinanceMutated);
        var again = loop.HoldStrip(projectId, "act-hold-2");
        Assert.Equal(held.CurrentStripDays, again.CurrentStripDays);
    }

    [Fact]
    public async Task FoundationPourDemo_MissingRequiredInputs_SilencesLoop()
    {
        var (demo, loop) = CreateDemoService();
        var projectId = $"test-pour-incomplete-{Guid.NewGuid():N}";

        var result = await demo.RunAsync(new FoundationPourDemoOptions
        {
            IncludeRequiredPhysics = false,
            SeedAdditionalVerifications = 0,
            ProjectId = projectId
        });

        Assert.True(result.Incomplete);
        Assert.Empty(result.Hypotheses);
        Assert.False(result.LoopClosed);
        Assert.Equal("", result.ChosenHypothesisId);
        Assert.Contains("Missing", result.IncompleteReason, StringComparison.Ordinal);
        Assert.Empty(loop.ListControlRecommendations(projectId));
        Assert.True(result.PedagogySilence);
        Assert.Null(result.PedagogyProposedAction);
        Assert.False(result.PedagogyRecorded);
        Assert.Empty(loop.ListFieldLessons(projectId));
    }

    [Fact]
    public async Task FoundationPourDemo_ContradictoryEvidence_SilencesLoop()
    {
        var (demo, loop) = CreateDemoService();
        var projectId = $"test-pour-faulted-{Guid.NewGuid():N}";

        var result = await demo.RunAsync(new FoundationPourDemoOptions
        {
            ProjectId = projectId,
            SeedAdditionalVerifications = 0,
            DecisionId = "decision-faulted",
            VerificationId = "verify-faulted",
            EvidenceJson = """{"contradictory":true}"""
        });

        Assert.True(result.Incomplete);
        Assert.Empty(result.Hypotheses);
        Assert.False(result.LoopClosed);
        Assert.Contains("contradictory", result.IncompleteReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(loop.ListControlRecommendations(projectId));
    }

    [Fact]
    public async Task HypothesisEngine_CachesPourHypotheses_ForVerificationWithoutAtlas()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var atlas = new AtlasService(config, NullLogger<AtlasService>.Instance);
        var engine = new HypothesisEngine(atlas, NullLogger<HypothesisEngine>.Instance);
        var verification = new PhysicalVerificationService(atlas, engine, NullLogger<PhysicalVerificationService>.Instance);

        var comparison = await engine.GenerateHypothesesAsync(
            "Foundation pour for 4000 PSI slab in cold weather",
            "foundation-pour",
            "unit-test-project",
            new Dictionary<string, object>
            {
                ["target_psi"] = 4000,
                ["ambient_temp_f"] = 38,
                ["slab_thickness_in"] = 8
            });

        var chosen = comparison.Hypotheses[0];
        var found = await engine.FindHypothesisByIdAsync(chosen.HypothesisId);
        Assert.NotNull(found);

        var actual = chosen.QuantitativePredictions.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value * 0.7);

        var result = await verification.VerifyPredictionAsync(
            chosen.HypothesisId,
            "Under-strength cylinders at 7 days",
            actual,
            verifiedBy: "unit-test");

        Assert.True(result.RequiresModelCorrection);
        Assert.NotEmpty(result.MeasurementVariances);
    }
}
