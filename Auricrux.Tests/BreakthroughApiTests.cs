using System.Net;
using System.Net.Http.Json;
using Auricrux.Web.Services.Breakthrough;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Auricrux.Tests;

/// <summary>
/// Exercises the breakthrough API surface end to end with no Atlas configured:
/// hypotheses → verification → accuracy → meta-learning → provable reasoning.
/// </summary>
public sealed class BreakthroughApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public BreakthroughApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(_ => { }).CreateClient();
    }

    [Fact]
    public async Task Hypotheses_endpoint_returns_competing_approaches_with_predictions()
    {
        var response = await _client.PostAsJsonAsync("/api/breakthrough/hypotheses", new
        {
            decisionContext = "Foundation pour for 8-inch slab, 4000 PSI, overnight low near freezing",
            constructionPhase = "foundation-pour",
            projectId = $"api-test-{Guid.NewGuid():N}",
            constraints = new Dictionary<string, double>
            {
                ["target_psi"] = 4000,
                ["ambient_temp_f"] = 38,
                ["slab_thickness_in"] = 8
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var comparison = await response.Content.ReadFromJsonAsync<HypothesisComparison>();

        Assert.NotNull(comparison);
        Assert.True(comparison!.Hypotheses.Count >= 3);
        Assert.False(string.IsNullOrWhiteSpace(comparison.RecommendedApproach));
        Assert.All(comparison.Hypotheses, h => Assert.NotEmpty(h.QuantitativePredictions));
    }

    [Fact]
    public async Task Hypotheses_endpoint_rejects_missing_context()
    {
        var response = await _client.PostAsJsonAsync("/api/breakthrough/hypotheses", new
        {
            decisionContext = "",
            constructionPhase = "foundation-pour"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Generated_hypotheses_are_retrievable_and_verifiable_without_atlas()
    {
        var generate = await _client.PostAsJsonAsync("/api/breakthrough/hypotheses", new
        {
            decisionContext = "Cold weather foundation pour with tight truck spacing",
            constructionPhase = "foundation-pour",
            constraints = new Dictionary<string, double> { ["target_psi"] = 4000, ["ambient_temp_f"] = 40 }
        });
        var comparison = await generate.Content.ReadFromJsonAsync<HypothesisComparison>();
        Assert.NotNull(comparison);

        var fetched = await _client.GetAsync($"/api/breakthrough/hypotheses/{comparison!.DecisionId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);

        var chosen = comparison.Hypotheses[0];
        var underPerforming = chosen.QuantitativePredictions
            .ToDictionary(kv => kv.Key, kv => kv.Value * 0.6);

        var verify = await _client.PostAsJsonAsync("/api/breakthrough/verify", new
        {
            predictionId = chosen.HypothesisId,
            actualOutcome = "Cylinders broke low at 7 days; stripping delayed",
            actualMeasurements = underPerforming,
            verifiedBy = "api-test"
        });

        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var result = await verify.Content.ReadFromJsonAsync<PhysicalVerificationResult>();

        Assert.NotNull(result);
        Assert.True(result!.RequiresModelCorrection);
        Assert.NotEmpty(result.MeasurementVariances);
        Assert.True(result.AccuracyScore < 1.0);
    }

    [Fact]
    public async Task Verify_endpoint_rejects_empty_measurements()
    {
        var response = await _client.PostAsJsonAsync("/api/breakthrough/verify", new
        {
            predictionId = "does-not-matter",
            actualMeasurements = new Dictionary<string, double>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_decision_returns_not_found()
    {
        var response = await _client.GetAsync($"/api/breakthrough/hypotheses/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Provable_reasoning_returns_steps_and_standards()
    {
        var response = await _client.PostAsJsonAsync("/api/breakthrough/provable-reasoning", new
        {
            question = "Will a 4000 PSI foundation pour reach stripping strength in 7 days at 45F?",
            physicalParameters = new Dictionary<string, double>
            {
                ["target_psi"] = 4000,
                ["ambient_temp_f"] = 45,
                ["required_strip_psi"] = 2800
            },
            applicableCodes = new[] { "ACI 318", "ACI 301", "ACI 306" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proof = await response.Content.ReadFromJsonAsync<ProvableReasoningResult>();

        Assert.NotNull(proof);
        Assert.False(string.IsNullOrWhiteSpace(proof!.Conclusion));
        Assert.NotEmpty(proof.ProofSteps);
        Assert.NotEmpty(proof.CitedStandards);
        Assert.Contains("ACI 209R", string.Join(' ', proof.CitedStandards), StringComparison.OrdinalIgnoreCase);
        Assert.True(proof.MathematicalVerification.ContainsKey("unprotected_7d_psi"));

        var fetched = await _client.GetAsync($"/api/breakthrough/provable-reasoning/{proof.ProofId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task Provable_reasoning_pile_capacity_scales_with_length()
    {
        var shortPile = await _client.PostAsJsonAsync("/api/breakthrough/provable-reasoning", new
        {
            question = "What is the driven pile capacity?",
            physicalParameters = new Dictionary<string, double> { ["pile_length_ft"] = 20, ["pile_diameter_in"] = 12 }
        });
        var longPile = await _client.PostAsJsonAsync("/api/breakthrough/provable-reasoning", new
        {
            question = "What is the driven pile capacity?",
            physicalParameters = new Dictionary<string, double> { ["pile_length_ft"] = 80, ["pile_diameter_in"] = 12 }
        });

        var shortProof = await shortPile.Content.ReadFromJsonAsync<ProvableReasoningResult>();
        var longProof = await longPile.Content.ReadFromJsonAsync<ProvableReasoningResult>();
        var shortQ = double.Parse(shortProof!.MathematicalVerification["qall_lbs"]);
        var longQ = double.Parse(longProof!.MathematicalVerification["qall_lbs"]);
        Assert.True(longQ > shortQ);
    }

    [Fact]
    public async Task Provable_reasoning_rejects_missing_question()
    {
        var response = await _client.PostAsJsonAsync("/api/breakthrough/provable-reasoning", new { question = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Accuracy_and_meta_learning_endpoints_respond_without_atlas()
    {
        var accuracy = await _client.GetAsync("/api/breakthrough/accuracy?periodHours=168");
        Assert.Equal(HttpStatusCode.OK, accuracy.StatusCode);

        var meta = await _client.GetAsync("/api/breakthrough/meta-learning/auricrux-primary?periodHours=168");
        Assert.Equal(HttpStatusCode.OK, meta.StatusCode);
        var insight = await meta.Content.ReadFromJsonAsync<MetaLearningInsight>();
        Assert.NotNull(insight);

        var recommendations = await _client.GetAsync("/api/breakthrough/improvement-recommendations");
        Assert.Equal(HttpStatusCode.OK, recommendations.StatusCode);
    }

    [Fact]
    public async Task Dashboard_reports_in_memory_breakthrough_instead_of_bare_zeros()
    {
        // Guarantee at least one verification exists in the in-process cache.
        await _client.PostAsync("/api/breakthrough/demo/foundation-pour", content: null);

        var response = await _client.GetAsync("/api/intelligence/dashboard/breakthrough?period=7d");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var activity = await response.Content.ReadFromJsonAsync<BreakthroughActivityPayload>();
        Assert.NotNull(activity);
        Assert.Equal("in-memory", activity!.Persistence);
        Assert.True(activity.VerificationsRecorded > 0);

        var overview = await _client.GetAsync("/api/intelligence/dashboard/overview?period=7d");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        var payload = await overview.Content.ReadFromJsonAsync<OverviewPayload>();
        Assert.NotNull(payload);
        Assert.Equal("in_memory_breakthrough", payload!.Status);
        Assert.False(string.IsNullOrWhiteSpace(payload.StatusMessage));
        Assert.NotEqual("healthy", payload.Health.OllamaStatus);
        Assert.False(string.IsNullOrWhiteSpace(payload.Health.RuntimeMode));
    }

    [Fact]
    public async Task Predictive_recommendations_are_empty_not_a_placeholder()
    {
        var response = await _client.GetAsync("/api/predictive/recommendations/demo-site");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("implementation in progress", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("predictive_recommendations", body);
        Assert.Contains("atlas_configured", body);
    }

    [Fact]
    public async Task Foundation_pile_hypotheses_scale_capacity_with_pile_length()
    {
        var shortResp = await _client.PostAsJsonAsync("/api/breakthrough/hypotheses", new
        {
            decisionContext = "Driven piles for warehouse column loads",
            constructionPhase = "foundation",
            constraints = new Dictionary<string, double> { ["pile_length_ft"] = 20 }
        });
        var longResp = await _client.PostAsJsonAsync("/api/breakthrough/hypotheses", new
        {
            decisionContext = "Driven piles for warehouse column loads",
            constructionPhase = "foundation",
            constraints = new Dictionary<string, double> { ["pile_length_ft"] = 80 }
        });

        var shortCmp = await shortResp.Content.ReadFromJsonAsync<HypothesisComparison>();
        var longCmp = await longResp.Content.ReadFromJsonAsync<HypothesisComparison>();
        var shortPile = shortCmp!.Hypotheses.First(h => h.Approach.Contains("Driven Pile", StringComparison.Ordinal));
        var longPile = longCmp!.Hypotheses.First(h => h.Approach.Contains("Driven Pile", StringComparison.Ordinal));

        Assert.NotEqual(85, shortPile.QuantitativePredictions["load_capacity_tons"]);
        Assert.True(longPile.QuantitativePredictions["load_capacity_tons"] >
                    shortPile.QuantitativePredictions["load_capacity_tons"]);
    }

    [Fact]
    public async Task Structural_hypotheses_include_deflection_check()
    {
        var response = await _client.PostAsJsonAsync("/api/breakthrough/hypotheses", new
        {
            decisionContext = "Steel framing for a 30-foot bay",
            constructionPhase = "structural-steel",
            constraints = new Dictionary<string, double> { ["span_ft"] = 30, ["uniform_load_plf"] = 400 }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var comparison = await response.Content.ReadFromJsonAsync<HypothesisComparison>();
        Assert.All(comparison!.Hypotheses, h =>
        {
            Assert.True(h.QuantitativePredictions.ContainsKey("beam_deflection_inches"));
            Assert.True(h.QuantitativePredictions.ContainsKey("deflection_ok"));
        });
    }

    [Fact]
    public async Task Pour_demo_includes_hot_weather_strategy_and_writes_control_recommendations()
    {
        var projectId = $"nsf-pour-{Guid.NewGuid():N}";
        var demo = await _client.PostAsJsonAsync("/api/breakthrough/demo/foundation-pour", new
        {
            projectId,
            targetPsi = 4000,
            ambientTempF = 95,
            slabThicknessIn = 8,
            relativeHumidity = 0.2,
            windSpeedMph = 18,
            seedAdditionalVerifications = 10
        });
        Assert.Equal(HttpStatusCode.OK, demo.StatusCode);
        var body = await demo.Content.ReadAsStringAsync();
        Assert.Contains("Hot-Weather", body, StringComparison.Ordinal);

        var recs = await _client.GetAsync($"/api/predictive/recommendations/{projectId}");
        Assert.Equal(HttpStatusCode.OK, recs.StatusCode);
        var recBody = await recs.Content.ReadAsStringAsync();
        Assert.DoesNotContain("implementation in progress", recBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("from_breakthrough_loop", recBody);
    }

    [Fact]
    public async Task Pile_demo_closes_nsf_loop()
    {
        var response = await _client.PostAsJsonAsync("/api/breakthrough/demo/driven-pile", new
        {
            projectId = $"pile-{Guid.NewGuid():N}",
            pileLengthFt = 40,
            seedAdditionalVerifications = 10
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("load_capacity_tons", body);
        Assert.Contains("loopClosed", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class BreakthroughActivityPayload
    {
        public string Persistence { get; set; } = "";
        public int VerificationsRecorded { get; set; }
        public double AverageAccuracy { get; set; }
    }

    private sealed class OverviewPayload
    {
        public string Status { get; set; } = "";
        public string StatusMessage { get; set; } = "";
        public int OutcomesVerified { get; set; }
        public HealthPayload Health { get; set; } = new();
    }

    private sealed class HealthPayload
    {
        public string OllamaStatus { get; set; } = "";
        public string RuntimeMode { get; set; } = "";
        public string AtlasStatus { get; set; } = "";
    }
}
