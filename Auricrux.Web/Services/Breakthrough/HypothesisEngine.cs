using Auricrux.Web.Services;
using Auricrux.Web.Services.Breakthrough.Physics;
using Auricrux.Web.Services.PhaseI;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Auricrux.Web.Services.Breakthrough;

/// <summary>
/// PHASE 4 BREAKTHROUGH: Hypothesis Engine
/// 
/// Generates COMPETING construction hypotheses for decisions, not just one answer.
/// Each hypothesis includes:
/// - Different construction approaches
/// - Quantitative predictions (cost, time, safety metrics)
/// - Explicit assumptions
/// - Risk factors
/// 
/// This enables:
/// 1. Side-by-side comparison of approaches BEFORE execution
/// 2. Explicit capture of what Auricrux predicted
/// 3. Later verification against actual outcomes
/// 4. Detection of which approaches Auricrux consistently gets wrong
/// 
/// INNOVATION: No other construction AI generates falsifiable, competing hypotheses.
/// </summary>
public sealed class HypothesisEngine
{
    private readonly AtlasService _atlas;
    private readonly BreakthroughLoopStore _loop;
    private readonly ILogger<HypothesisEngine> _logger;

    public HypothesisEngine(AtlasService atlas, ILogger<HypothesisEngine> logger, BreakthroughLoopStore? loopStore = null)
    {
        _atlas = atlas;
        _loop = loopStore ?? new BreakthroughLoopStore();
        _logger = logger;
    }

    /// <summary>
    /// Generate multiple competing hypotheses for a construction decision
    /// </summary>
    public async Task<HypothesisComparison> GenerateHypothesesAsync(
        string decisionContext,
        string constructionPhase,
        string? projectId = null,
        Dictionary<string, object>? knownConstraints = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Generating competing hypotheses for decision in phase: {Phase}", constructionPhase);

        var decisionId = Guid.NewGuid().ToString();
        var hypotheses = await GenerateCompetingApproachesAsync(decisionContext, constructionPhase, knownConstraints, ct);
        var incompleteReason = RequiredPhysicsInputs.IncompleteReason(
            constructionPhase, decisionContext, knownConstraints, hypotheses.Count);

        var comparison = new HypothesisComparison
        {
            DecisionId = decisionId,
            DecisionContext = decisionContext,
            ConstructionPhase = constructionPhase,
            ProjectId = projectId,
            Hypotheses = hypotheses,
            RecommendedApproach = SelectRecommendedApproach(hypotheses),
            Reasoning = incompleteReason ?? BuildRecommendationReasoning(hypotheses),
            GeneratedAt = DateTime.UtcNow,
            Incomplete = incompleteReason is not null,
            IncompleteReason = incompleteReason
        };

        CacheInMemory(comparison);

        // Persist to Atlas for later verification
        if (_atlas.IsConfigured)
        {
            await PersistHypothesesAsync(comparison, ct);
        }

        return comparison;
    }

    /// <summary>
    /// Look up a single hypothesis by id (memory first, then Atlas).
    /// Used by PhysicalVerificationService when closing the self-correction loop.
    /// </summary>
    public async Task<ConstructionHypothesis?> FindHypothesisByIdAsync(string hypothesisId, CancellationToken ct = default)
    {
        if (_loop.FindHypothesis(hypothesisId) is { } cached)
            return cached;

        if (!_atlas.IsConfigured) return null;

        try
        {
            var filter = Builders<BsonDocument>.Filter.ElemMatch<BsonValue>(
                "hypotheses",
                new BsonDocument { ["hypothesis_id"] = hypothesisId });

            var doc = await _atlas.Database!
                .GetCollection<BsonDocument>("hypothesis_comparisons")
                .Find(filter)
                .FirstOrDefaultAsync(ct);

            if (doc == null) return null;

            var hypothesisDoc = doc["hypotheses"].AsBsonArray
                .Select(h => h.AsBsonDocument)
                .FirstOrDefault(h => h["hypothesis_id"].AsString == hypothesisId);

            if (hypothesisDoc == null) return null;

            var hypothesis = MapHypothesisFromBson(hypothesisDoc);
            _loop.CacheHypothesis(hypothesis);
            return hypothesis;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving hypothesis {HypothesisId}", hypothesisId);
            return null;
        }
    }

    /// <summary>
    /// Retrieve a hypothesis comparison for verification
    /// </summary>
    public async Task<HypothesisComparison?> GetHypothesesAsync(string decisionId, CancellationToken ct = default)
    {
        if (_loop.FindDecision(decisionId) is { } cached)
            return cached;

        if (!_atlas.IsConfigured) return null;

        try
        {
            var filter = Builders<BsonDocument>.Filter.Eq("decision_id", decisionId);
            var doc = await _atlas.Database
                .GetCollection<BsonDocument>("hypothesis_comparisons")
                .Find(filter)
                .FirstOrDefaultAsync(ct);

            if (doc == null) return null;

            var mapped = MapFromBson(doc);
            CacheInMemory(mapped);
            return mapped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving hypotheses for decision {DecisionId}", decisionId);
            return null;
        }
    }

    private void CacheInMemory(HypothesisComparison comparison) => _loop.CacheComparison(comparison);

    private static ConstructionHypothesis MapHypothesisFromBson(BsonDocument hypothesisDoc) => new()
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

    // ── Private Implementation ──────────────────────────────────────────────────

    private async Task<List<ConstructionHypothesis>> GenerateCompetingApproachesAsync(
        string decisionContext,
        string phase,
        Dictionary<string, object>? constraints,
        CancellationToken ct)
    {
        // In production, this would use sophisticated construction knowledge + physics models
        // For now, demonstrate the structure with domain-grounded examples

        var hypotheses = new List<ConstructionHypothesis>();

        // Hypothesis generation is phase-specific
        if (decisionContext.Contains("pour", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("pour", StringComparison.OrdinalIgnoreCase))
        {
            hypotheses.AddRange(GenerateFoundationPourHypotheses(decisionContext, constraints));
        }
        else if (phase.Contains("foundation", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("concrete", StringComparison.OrdinalIgnoreCase))
        {
            hypotheses.AddRange(GenerateFoundationHypotheses(decisionContext, constraints));
        }
        else if (phase.Contains("steel", StringComparison.OrdinalIgnoreCase) ||
                 phase.Contains("structural", StringComparison.OrdinalIgnoreCase))
        {
            hypotheses.AddRange(GenerateStructuralHypotheses(decisionContext, constraints));
        }
        else if (phase.Contains("schedule", StringComparison.OrdinalIgnoreCase) ||
                 phase.Contains("delay", StringComparison.OrdinalIgnoreCase))
        {
            hypotheses.AddRange(GenerateScheduleHypotheses(decisionContext, constraints));
        }
        else
        {
            // Generic construction hypotheses
            hypotheses.AddRange(GenerateGenericHypotheses(decisionContext, constraints));
        }

        return hypotheses;
    }

    private List<ConstructionHypothesis> GenerateFoundationPourHypotheses(
        string context,
        Dictionary<string, object>? constraints)
    {
        if (RequiredPhysicsInputs.Missing(constraints, RequiredPhysicsInputs.PourRequired).Count > 0)
            return [];

        RequiredPhysicsInputs.TryRead(constraints, "target_psi", out var targetPsi);
        RequiredPhysicsInputs.TryRead(constraints, "ambient_temp_f", out var ambientTempF);
        RequiredPhysicsInputs.TryRead(constraints, "slab_thickness_in", out var slabThicknessIn);
        var relativeHumidity = RequiredPhysicsInputs.TryRead(constraints, "relative_humidity", out var humidity)
            ? humidity
            : 0.55;
        var windSpeedMph = RequiredPhysicsInputs.TryRead(constraints, "wind_speed_mph", out var mph)
            ? mph
            : 8.0;

        return
        [
            BuildPourHypothesis(
                FoundationPourPhysics.PourStrategy.StandardAmbient,
                "Standard 4000 PSI Mix — Day Pour, Ambient Cure",
                "Meets design strength at 28 days with normal finishing window",
                [
                    "Water-cement ratio held to mix design",
                    "Vibration and finishing per ACI 301",
                    "No extended truck wait beyond 90 minutes"
                ],
                baseConfidence: 0.84,
                [
                    "Hot-weather set acceleration if temps rise",
                    "Finishers overworking surface → scaling",
                    "Supply delay creating cold joints"
                ],
                targetPsi, ambientTempF, slabThicknessIn, relativeHumidity, windSpeedMph),

            BuildPourHypothesis(
                FoundationPourPhysics.PourStrategy.ColdWeatherProtected,
                "Cold-Weather Pour — Heated Mix + Insulated Blankets",
                "Protected early strength; slightly longer schedule, lower freeze risk",
                [
                    "Mix water heated; aggregates above freezing",
                    "Insulated blankets placed within 1 hour of finish",
                    "Cylinder cures match field protection protocol",
                    "ACI 306R cold-weather plan active"
                ],
                baseConfidence: 0.79,
                [
                    "Blanket gaps causing surface freeze",
                    "Under-heated mix slowing set",
                    "Cylinder strength not matching field if protection removed early"
                ],
                targetPsi, ambientTempF, slabThicknessIn, relativeHumidity, windSpeedMph),

            BuildPourHypothesis(
                FoundationPourPhysics.PourStrategy.AcceleratedHighEarly,
                "Accelerated High-Early Mix — Fast Form Cycle",
                "Earlier stripping strength; higher cost and thermal cracking risk",
                [
                    "Type III or accelerator dosage within manufacturer limits",
                    "Thermal control plan for mass sections",
                    "QA cylinders cast for 1-day and 3-day breaks",
                    "Finishing crew sized for faster set"
                ],
                baseConfidence: 0.71,
                [
                    "Plastic shrinkage if wind/sun high",
                    "Thermal cracking in thicker sections",
                    "Cost overrun from admixture premium"
                ],
                targetPsi, ambientTempF, slabThicknessIn, relativeHumidity, windSpeedMph),

            BuildPourHypothesis(
                FoundationPourPhysics.PourStrategy.HotWeatherProtected,
                "Hot-Weather Pour — Fogging, Shades, Cooled Mix (ACI 305R)",
                "Lower plastic-shrinkage and cold-joint risk; slightly slower surface set than an unprotected hot pour",
                [
                    "Fog spray or evaporation retarder on the surface",
                    "Sunshades and cooled mixing water per ACI 305R",
                    "Truck intervals planned inside the shortened set window",
                    "Finishers staged before the slab goes plastic"
                ],
                baseConfidence: 0.76,
                [
                    "Fogging interrupted by wind",
                    "Retarder overdose delaying strip",
                    "Crew not ready when the surface crusts"
                ],
                targetPsi, ambientTempF, slabThicknessIn, relativeHumidity, windSpeedMph)
        ];
    }

    /// <summary>
    /// Builds one pour hypothesis whose quantitative predictions come from ACI-based
    /// concrete and weather physics rather than fixed ratios, so field verification can
    /// falsify the physics rather than a guess.
    /// </summary>
    private static ConstructionHypothesis BuildPourHypothesis(
        FoundationPourPhysics.PourStrategy strategy,
        string approach,
        string predictedOutcome,
        List<string> assumptions,
        double baseConfidence,
        List<string> riskFactors,
        double targetPsi,
        double ambientTempF,
        double slabThicknessIn,
        double relativeHumidity,
        double windSpeedMph)
    {
        var physics = FoundationPourPhysics.Predict(
            strategy, targetPsi, ambientTempF, slabThicknessIn, relativeHumidity, windSpeedMph);

        var groundedAssumptions = new List<string>(assumptions)
        {
            $"Effective cure temperature {physics.EffectiveCureTempF:0.#}°F (ambient {ambientTempF:0.#}°F)",
            $"Surface evaporation {physics.EvaporationRateLbPerSqFtPerHour:0.###} lb/ft²/hr (ACI 305R)",
            $"Strength gain per ACI 209R; stripping at {FoundationPourPhysics.StrippingStrengthFraction:P0} of design"
        };

        var groundedRisks = new List<string>(riskFactors);
        var confidence = baseConfidence;

        if (physics.ColdProtectionRequired)
        {
            if (strategy == FoundationPourPhysics.PourStrategy.ColdWeatherProtected)
            {
                confidence += 0.08;
            }
            else
            {
                confidence -= 0.15;
                groundedRisks.Insert(
                    0,
                    $"ACI 306R cold-weather protection triggered at {ambientTempF:0.#}°F — unprotected cure loses early strength");
            }
        }

        if (physics.EvaporationRateLbPerSqFtPerHour > FoundationPourPhysics.HighEvaporationThreshold
            || physics.HotWeatherProtectionRequired)
        {
            if (strategy == FoundationPourPhysics.PourStrategy.HotWeatherProtected)
            {
                confidence += 0.08;
            }
            else
            {
                confidence -= 0.05;
                groundedRisks.Insert(
                    0,
                    $"Evaporation {physics.EvaporationRateLbPerSqFtPerHour:0.###} lb/ft²/hr / {ambientTempF:0.#}°F — ACI 305R hot-weather controls not in this approach");
            }
        }

        if (strategy == FoundationPourPhysics.PourStrategy.AcceleratedHighEarly && slabThicknessIn > 18)
        {
            confidence -= 0.05;
            groundedRisks.Insert(0, $"Mass section at {slabThicknessIn:0.#} in raises thermal-gradient cracking risk");
        }

        return new ConstructionHypothesis
        {
            HypothesisId = Guid.NewGuid().ToString(),
            Approach = approach,
            PredictedOutcome = predictedOutcome,
            Assumptions = groundedAssumptions,
            QuantitativePredictions = new Dictionary<string, double>
            {
                { "compressive_strength_psi_7d", physics.Strength7dPsi },
                { "compressive_strength_psi_28d", physics.Strength28dPsi },
                { "slump_inches", physics.SlumpInches },
                { "cure_days_to_stripping", physics.CureDaysToStripping },
                { "cold_joint_risk_percent", physics.ColdJointRiskPercent }
            },
            ConfidenceScore = Math.Clamp(confidence, 0.35, 0.95),
            RiskFactors = groundedRisks
        };
    }

    private static double ReadNumericConstraint(
        Dictionary<string, object>? constraints,
        string key,
        double fallback)
    {
        if (constraints != null &&
            constraints.TryGetValue(key, out var raw) &&
            double.TryParse(raw?.ToString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private List<ConstructionHypothesis> GenerateFoundationHypotheses(
        string context,
        Dictionary<string, object>? constraints)
    {
        var cohesion = ReadNumericConstraint(constraints, "cohesion_psf", 200);
        var phi = ReadNumericConstraint(constraints, "friction_angle_deg", 30);
        var unitWeight = ReadNumericConstraint(constraints, "unit_weight_pcf", 120);
        var widthFt = ReadNumericConstraint(constraints, "footing_width_ft", 6);
        var depthFt = ReadNumericConstraint(constraints, "footing_depth_ft", 4);
        var pileDiaIn = ReadNumericConstraint(constraints, "pile_diameter_in", 12);
        var pileLenFt = ReadNumericConstraint(constraints, "pile_length_ft", 40);
        var skinPsf = ReadNumericConstraint(constraints, "skin_friction_psf", 800);
        var endBearingPsf = ReadNumericConstraint(constraints, "end_bearing_psf", 8000);
        var elasticModulusPsf = ReadNumericConstraint(constraints, "elastic_modulus_psf", 400_000);
        var appliedPsf = ReadNumericConstraint(constraints, "applied_pressure_psf", 2500);

        var pileCapacityTons = FoundationPhysicsModel.DrivenPileCapacity(
            pileDiaIn, pileLenFt, skinPsf, endBearingPsf) / 2000.0;
        var shaftCapacityTons = FoundationPhysicsModel.DrivenPileCapacity(
            pileDiaIn * 2.5, pileLenFt * 1.15, skinPsf * 0.9, endBearingPsf * 1.15) / 2000.0;
        var qAllow = FoundationPhysicsModel.AllowableBearingCapacity(
            FoundationPhysicsModel.FootingShape.Square,
            widthFt, depthFt, cohesion, phi, unitWeight);
        var settlementIn = FoundationPhysicsModel.ImmediateSettlement(
            appliedPsf, widthFt, depthFt, elasticModulusPsf);
        var (ka, kp) = FoundationPhysicsModel.LateralEarthPressureCoefficients(phi);

        var pileSettlementRisk = Math.Clamp(2 + pileLenFt / 20, 2, 12);
        var shaftSettlementRisk = Math.Max(1, pileSettlementRisk - 3);
        var footingSettlementRisk = Math.Clamp(settlementIn / 0.5 * 10, 4, 40);
        var footingCapacityTons = qAllow * widthFt * widthFt / 2000.0;

        return
        [
            new ConstructionHypothesis
            {
                HypothesisId = Guid.NewGuid().ToString(),
                Approach = "Standard Driven Pile Foundation",
                PredictedOutcome = "High load capacity with moderate installation time and cost",
                Assumptions =
                [
                    $"Meyerhof pile capacity on {pileDiaIn:0.#} in × {pileLenFt:0.#} ft piles",
                    $"Skin friction {skinPsf:0} psf, end bearing {endBearingPsf:0} psf",
                    "Access for pile driving equipment available",
                    "Vibration tolerance acceptable for surroundings"
                ],
                QuantitativePredictions = new Dictionary<string, double>
                {
                    { "cost_per_pile_usd", 8500 },
                    { "installation_days", 12 },
                    { "load_capacity_tons", Math.Round(pileCapacityTons, 1) },
                    { "settlement_risk_percent", Math.Round(pileSettlementRisk, 1) },
                    { "active_earth_pressure_ka", Math.Round(ka, 3) }
                },
                ConfidenceScore = 0.82,
                RiskFactors =
                [
                    "Vibration impact on adjacent structures",
                    "Encountering unexpected bedrock depth",
                    "Weather delays during installation"
                ]
            },
            new ConstructionHypothesis
            {
                HypothesisId = Guid.NewGuid().ToString(),
                Approach = "Drilled Shaft (Caisson) Foundation",
                PredictedOutcome = "Highest load capacity with lower vibration but higher cost",
                Assumptions =
                [
                    $"Larger shaft section ({pileDiaIn * 2.5:0.#} in) using same soil parameters",
                    "Soil conditions allow for open-hole drilling",
                    "Dewatering available if needed",
                    "Concrete truck access confirmed"
                ],
                QuantitativePredictions = new Dictionary<string, double>
                {
                    { "cost_per_shaft_usd", 14200 },
                    { "installation_days", 18 },
                    { "load_capacity_tons", Math.Round(shaftCapacityTons, 1) },
                    { "settlement_risk_percent", Math.Round(shaftSettlementRisk, 1) },
                    { "passive_earth_pressure_kp", Math.Round(kp, 3) }
                },
                ConfidenceScore = 0.75,
                RiskFactors =
                [
                    "Caving during drilling if unstable soil",
                    "Concrete placement quality issues",
                    "Higher cost overrun risk",
                    "Weather impact on open holes"
                ]
            },
            new ConstructionHypothesis
            {
                HypothesisId = Guid.NewGuid().ToString(),
                Approach = "Shallow Spread Footing with Ground Improvement",
                PredictedOutcome = "Cost-effective if soil improvement successful, higher schedule risk",
                Assumptions =
                [
                    $"Terzaghi allowable bearing {qAllow:0} psf (FS=3) on {widthFt:0.#} ft square footing",
                    $"Immediate settlement {settlementIn:0.###} in (elastic)",
                    "Soil improvement (compaction/stone columns) achieves target bearing",
                    "Settlement monitoring acceptable"
                ],
                QuantitativePredictions = new Dictionary<string, double>
                {
                    { "cost_per_footing_usd", 5800 },
                    { "total_days_with_improvement", 24 },
                    { "load_capacity_tons", Math.Round(footingCapacityTons, 1) },
                    { "settlement_risk_percent", Math.Round(footingSettlementRisk, 1) },
                    { "allowable_bearing_psf", Math.Round(qAllow, 0) },
                    { "predicted_settlement_inches", Math.Round(settlementIn, 3) }
                },
                ConfidenceScore = 0.68,
                RiskFactors =
                [
                    "Ground improvement may not achieve target density",
                    "Differential settlement between footings",
                    "Requires extensive testing and verification",
                    "Settlement monitoring long-term"
                ]
            }
        ];
    }

    private List<ConstructionHypothesis> GenerateStructuralHypotheses(
        string context,
        Dictionary<string, object>? constraints)
    {
        if (RequiredPhysicsInputs.Missing(constraints, RequiredPhysicsInputs.SteelDeflectionRequired).Count > 0)
            return [];

        RequiredPhysicsInputs.TryRead(constraints, "span_ft", out var spanFt);
        RequiredPhysicsInputs.TryRead(constraints, "uniform_load_plf", out var uniformLoadPlf);
        var elasticModulusPsi = RequiredPhysicsInputs.TryRead(constraints, "steel_E_psi", out var ePsi)
            ? ePsi
            : RequiredPhysicsInputs.SteelEPsiDefault;
        RequiredPhysicsInputs.TryRead(constraints, "moment_of_inertia_in4", out var momentOfInertiaIn4);
        var plasticModulusIn3 = ReadNumericConstraint(constraints, "plastic_modulus_in3", 47);
        var fyPsi = ReadNumericConstraint(constraints, "fy_psi", 50_000);
        var boltDiaIn = ReadNumericConstraint(constraints, "bolt_diameter_in", 0.75);
        var boltFuPsi = ReadNumericConstraint(constraints, "bolt_Fu_psi", 120_000);

        var deflectionIn = StructuralPhysicsModel.BeamDeflectionUniformLoad(
            uniformLoadPlf, spanFt, elasticModulusPsi, momentOfInertiaIn4);
        var deflectionOk = StructuralPhysicsModel.IsDeflectionAcceptable(deflectionIn, spanFt);
        var boltShearLbs = StructuralPhysicsModel.BoltShearCapacity(boltDiaIn, boltFuPsi);
        var phiMn = StructuralPhysicsModel.DesignFlexuralStrength(
            StructuralPhysicsModel.SteelBeamFlexuralStrength(fyPsi, plasticModulusIn3));
        var combo = StructuralPhysicsModel.LoadCombination(
            deadLoad: uniformLoadPlf * 0.4,
            liveLoad: uniformLoadPlf * 0.6,
            loadCase: StructuralPhysicsModel.LoadCombinationCase.LRFD_1_2D_1_6L);

        var confidenceConventional = deflectionOk ? 0.88 : 0.62;
        var confidenceModular = deflectionOk ? 0.72 : 0.55;

        return
        [
            new ConstructionHypothesis
            {
                HypothesisId = Guid.NewGuid().ToString(),
                Approach = "Conventional Steel Erection Sequence",
                PredictedOutcome = deflectionOk
                    ? "Proven approach with predictable timeline; beam deflection within L/360"
                    : "Erection is feasible but live-load deflection exceeds L/360 — member size should increase",
                Assumptions =
                [
                    $"Simply supported span {spanFt:0.#} ft, w = {uniformLoadPlf:0} plf",
                    $"AISC compact section, Fy = {fyPsi / 1000:0} ksi, I = {momentOfInertiaIn4:0} in⁴",
                    "Steel fabrication on schedule",
                    "Bolted connections per AISC/RCSC spec"
                ],
                QuantitativePredictions = new Dictionary<string, double>
                {
                    { "erection_days", 45 },
                    { "crew_size", 8 },
                    { "cost_per_ton_usd", 2400 },
                    { "safety_incidents_risk_percent", 8 },
                    { "beam_deflection_inches", Math.Round(deflectionIn, 3) },
                    { "deflection_ok", deflectionOk ? 1 : 0 },
                    { "bolt_shear_capacity_lbs", Math.Round(boltShearLbs, 0) },
                    { "design_flexural_in_lb", Math.Round(phiMn, 0) },
                    { "lrfd_1_2D_1_6L_plf", Math.Round(combo, 1) }
                },
                ConfidenceScore = confidenceConventional,
                RiskFactors = deflectionOk
                    ? ["Weather delays", "Crane breakdown", "Bolt torque QC failures"]
                    : ["Live-load deflection exceeds L/360", "Weather delays", "Bolt torque QC failures"]
            },
            new ConstructionHypothesis
            {
                HypothesisId = Guid.NewGuid().ToString(),
                Approach = "Accelerated Modular Steel Assembly",
                PredictedOutcome = "Faster erection but higher coordination complexity",
                Assumptions =
                [
                    "Ground-level pre-assembly space available",
                    "Larger crane capacity for module lifts",
                    "Field welding crew coordinated",
                    "Module fit-up tolerance within 1/8\""
                ],
                QuantitativePredictions = new Dictionary<string, double>
                {
                    { "erection_days", 28 },
                    { "crew_size", 12 },
                    { "cost_per_ton_usd", 2750 },
                    { "safety_incidents_risk_percent", 5 },
                    { "beam_deflection_inches", Math.Round(deflectionIn, 3) },
                    { "deflection_ok", deflectionOk ? 1 : 0 },
                    { "bolt_shear_capacity_lbs", Math.Round(boltShearLbs, 0) }
                },
                ConfidenceScore = confidenceModular,
                RiskFactors =
                [
                    "Module fit-up issues",
                    "Field welding delays",
                    "Higher upfront coordination cost"
                ]
            }
        ];
    }

    private List<ConstructionHypothesis> GenerateScheduleHypotheses(
        string context,
        Dictionary<string, object>? constraints)
    {
        return new List<ConstructionHypothesis>
        {
            new ConstructionHypothesis
            {
                HypothesisId = Guid.NewGuid().ToString(),
                Approach = "Sequential Recovery with Float Reallocation",
                PredictedOutcome = "Methodical recovery, minimal additional cost",
                Assumptions = new List<string>
                {
                    "Float exists on critical path successor activities",
                    "No further delays occur",
                    "Resource leveling maintains productivity"
                },
                QuantitativePredictions = new Dictionary<string, double>
                {
                    { "recovery_days", 14 },
                    { "additional_cost_usd", 8500 },
                    { "success_probability_percent", 75 }
                },
                ConfidenceScore = 0.79,
                RiskFactors = new List<string>
                {
                    "Float may already be consumed",
                    "Subsequent delays compound recovery"
                }
            },
            new ConstructionHypothesis
            {
                HypothesisId = Guid.NewGuid().ToString(),
                Approach = "Crash Critical Path Activities",
                PredictedOutcome = "Fastest recovery but highest cost and risk",
                Assumptions = new List<string>
                {
                    "Overtime and double shifts available",
                    "Trade labor available",
                    "Quality does not degrade with acceleration"
                },
                QuantitativePredictions = new Dictionary<string, double>
                {
                    { "recovery_days", 7 },
                    { "additional_cost_usd", 42000 },
                    { "success_probability_percent", 60 }
                },
                ConfidenceScore = 0.65,
                RiskFactors = new List<string>
                {
                    "Crew fatigue and safety incidents",
                    "Quality issues from rushing",
                    "High cost may not be recoverable"
                }
            }
        };
    }

    private List<ConstructionHypothesis> GenerateGenericHypotheses(
        string context,
        Dictionary<string, object>? constraints)
    {
        return new List<ConstructionHypothesis>
        {
            new ConstructionHypothesis
            {
                HypothesisId = Guid.NewGuid().ToString(),
                Approach = "Standard Approach - By-the-Book Execution",
                PredictedOutcome = "Predictable outcome following industry standards",
                Assumptions = new List<string>
                {
                    "Plans and specifications are complete",
                    "Standard trade practices apply",
                    "No unforeseen site conditions"
                },
                QuantitativePredictions = new Dictionary<string, double>
                {
                    { "baseline_cost_multiplier", 1.0 },
                    { "baseline_schedule_multiplier", 1.0 },
                    { "risk_level_percent", 15 }
                },
                ConfidenceScore = 0.80,
                RiskFactors = new List<string>
                {
                    "Assumptions may not hold in field",
                    "Coordination gaps"
                }
            },
            new ConstructionHypothesis
            {
                HypothesisId = Guid.NewGuid().ToString(),
                Approach = "Accelerated Alternative Approach",
                PredictedOutcome = "Faster execution with trade-offs",
                Assumptions = new List<string>
                {
                    "Alternative methods are code-compliant",
                    "Resources available for acceleration",
                    "Quality can be maintained"
                },
                QuantitativePredictions = new Dictionary<string, double>
                {
                    { "baseline_cost_multiplier", 1.15 },
                    { "baseline_schedule_multiplier", 0.75 },
                    { "risk_level_percent", 25 }
                },
                ConfidenceScore = 0.68,
                RiskFactors = new List<string>
                {
                    "Higher cost",
                    "Increased coordination complexity"
                }
            }
        };
    }

    private string SelectRecommendedApproach(List<ConstructionHypothesis> hypotheses)
    {
        if (hypotheses.Count == 0)
            return "";
        var best = hypotheses.OrderByDescending(h => h.ConfidenceScore).First();
        return best.Approach;
    }

    private string BuildRecommendationReasoning(List<ConstructionHypothesis> hypotheses)
    {
        if (hypotheses.Count == 0)
            return "Prior is incomplete. Silence is the correct failure.";
        var recommended = hypotheses.OrderByDescending(h => h.ConfidenceScore).First();
        return $"Recommended '{recommended.Approach}' based on {recommended.ConfidenceScore:P0} confidence. " +
               $"This approach balances {string.Join(", ", recommended.Assumptions.Take(2))}. " +
               $"Key risks: {string.Join("; ", recommended.RiskFactors.Take(2))}.";
    }

    private async Task PersistHypothesesAsync(HypothesisComparison comparison, CancellationToken ct)
    {
        try
        {
            var doc = new BsonDocument
            {
                ["decision_id"] = comparison.DecisionId,
                ["decision_context"] = comparison.DecisionContext,
                ["construction_phase"] = comparison.ConstructionPhase,
                ["project_id"] = comparison.ProjectId ?? "",
                ["hypotheses"] = new BsonArray(comparison.Hypotheses.Select(h => new BsonDocument
                {
                    ["hypothesis_id"] = h.HypothesisId,
                    ["approach"] = h.Approach,
                    ["predicted_outcome"] = h.PredictedOutcome,
                    ["assumptions"] = new BsonArray(h.Assumptions),
                    ["quantitative_predictions"] = new BsonDocument(h.QuantitativePredictions.Select(kvp =>
                        new BsonElement(kvp.Key, kvp.Value))),
                    ["confidence_score"] = h.ConfidenceScore,
                    ["risk_factors"] = new BsonArray(h.RiskFactors)
                })),
                ["recommended_approach"] = comparison.RecommendedApproach,
                ["reasoning"] = comparison.Reasoning,
                ["generated_at"] = comparison.GeneratedAt,
                ["verified"] = false
            };

            await _atlas.Database
                .GetCollection<BsonDocument>("hypothesis_comparisons")
                .InsertOneAsync(doc, cancellationToken: ct);

            _logger.LogInformation("Persisted hypothesis comparison {DecisionId} with {Count} hypotheses",
                comparison.DecisionId, comparison.Hypotheses.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist hypothesis comparison");
        }
    }

    private HypothesisComparison MapFromBson(BsonDocument doc)
    {
        var hypotheses = doc["hypotheses"].AsBsonArray
            .Select(h => new ConstructionHypothesis
            {
                HypothesisId = h["hypothesis_id"].AsString,
                Approach = h["approach"].AsString,
                PredictedOutcome = h["predicted_outcome"].AsString,
                Assumptions = h["assumptions"].AsBsonArray.Select(a => a.AsString).ToList(),
                QuantitativePredictions = h["quantitative_predictions"].AsBsonDocument.ToDictionary(
                    e => e.Name,
                    e => e.Value.ToDouble()),
                ConfidenceScore = h["confidence_score"].ToDouble(),
                RiskFactors = h["risk_factors"].AsBsonArray.Select(r => r.AsString).ToList()
            })
            .ToList();

        return new HypothesisComparison
        {
            DecisionId = doc["decision_id"].AsString,
            DecisionContext = doc["decision_context"].AsString,
            ConstructionPhase = doc["construction_phase"].AsString,
            ProjectId = doc.GetValue("project_id", "").AsString,
            Hypotheses = hypotheses,
            RecommendedApproach = doc["recommended_approach"].AsString,
            Reasoning = doc["reasoning"].AsString,
            GeneratedAt = doc["generated_at"].ToUniversalTime()
        };
    }
}

// ── Models ──────────────────────────────────────────────────────────────────────

public sealed class HypothesisComparison
{
    public required string DecisionId { get; init; }
    public required string DecisionContext { get; init; }
    public required string ConstructionPhase { get; init; }
    public string? ProjectId { get; init; }
    public required List<ConstructionHypothesis> Hypotheses { get; init; }
    public required string RecommendedApproach { get; init; }
    public required string Reasoning { get; init; }
    public DateTime GeneratedAt { get; init; }
    public bool Incomplete { get; init; }
    public string? IncompleteReason { get; init; }
}

public sealed class ConstructionHypothesis
{
    public required string HypothesisId { get; init; }
    public required string Approach { get; init; }
    public required string PredictedOutcome { get; init; }
    public required List<string> Assumptions { get; init; }
    public required Dictionary<string, double> QuantitativePredictions { get; init; }
    public required double ConfidenceScore { get; init; }
    public required List<string> RiskFactors { get; init; }
}
