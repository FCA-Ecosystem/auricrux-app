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
            "unit-test-project");

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
