namespace Auricrux.Web.Services.Breakthrough.Physics;

/// <summary>
/// Derives foundation-pour predictions from concrete and weather physics instead of
/// fixed ratios, so hypotheses can be falsified against real ACI behavior.
///
/// Sources: ACI 209R-92 (strength gain), ACI 306R (cold weather), ACI 305R (evaporation).
/// </summary>
public static class FoundationPourPhysics
{
    /// <summary>Reference cure temperature the ACI 209R curve is calibrated against.</summary>
    public const double ReferenceCureTempF = 68.0;

    /// <summary>Fraction of design strength normally required before stripping forms.</summary>
    public const double StrippingStrengthFraction = 0.70;

    /// <summary>
    /// Evaporation level treated as high for pour planning.
    /// The inherited ACI 305R approximation in <see cref="WeatherPhysicsModel"/> returns values
    /// an order of magnitude above field lb/ft²/hr, so comparisons stay inside that model's scale.
    /// </summary>
    public const double HighEvaporationThreshold = 1.0;

    public enum PourStrategy
    {
        /// <summary>Ambient placement with normal Type I mix.</summary>
        StandardAmbient,

        /// <summary>Heated mix plus insulated blankets per ACI 306R.</summary>
        ColdWeatherProtected,

        /// <summary>Type III / accelerated mix for a fast form cycle.</summary>
        AcceleratedHighEarly,

        /// <summary>Fogging, sunshades, and cooled mix per ACI 305R hot-weather concreting.</summary>
        HotWeatherProtected
    }

    public sealed record PourPrediction(
        double Strength7dPsi,
        double Strength28dPsi,
        double SlumpInches,
        int CureDaysToStripping,
        double ColdJointRiskPercent,
        double EffectiveCureTempF,
        bool ColdProtectionRequired,
        bool HotWeatherProtectionRequired,
        double EvaporationRateLbPerSqFtPerHour);

    /// <summary>
    /// Predict pour outcomes for one strategy under given site conditions.
    /// </summary>
    public static PourPrediction Predict(
        PourStrategy strategy,
        double targetPsi,
        double ambientTempF,
        double slabThicknessIn,
        double relativeHumidity = 0.55,
        double windSpeedMph = 8)
    {
        var cementType = strategy == PourStrategy.AcceleratedHighEarly
            ? ConcretePhysicsModel.CementType.TypeIII
            : ConcretePhysicsModel.CementType.TypeI;

        var curing = strategy == PourStrategy.AcceleratedHighEarly
            ? ConcretePhysicsModel.CuringCondition.AirDried
            : ConcretePhysicsModel.CuringCondition.MoistCured;

        var coldProtectionRequired = WeatherPhysicsModel.RequiresColdWeatherProtection(
            meanDailyTempF: ambientTempF,
            minTempF: ambientTempF - 6,
            hoursBelow50F: ambientTempF < 50 ? 14 : 0);

        var evaporation = WeatherPhysicsModel.EstimateEvaporationRate(ambientTempF, relativeHumidity, windSpeedMph);
        var hotWeatherProtectionRequired = RequiresHotWeatherProtection(ambientTempF, evaporation);

        // Protected pours hold concrete at the ACI 306R minimum placement temperature.
        var effectiveCureTempF = ambientTempF;
        if (strategy == PourStrategy.ColdWeatherProtected)
        {
            var (minTempF, maxTempF) = WeatherPhysicsModel.ColdWeatherPlacementTemp(slabThicknessIn, ambientTempF);
            effectiveCureTempF = Math.Clamp(Math.Max(ambientTempF, minTempF), minTempF, maxTempF);
        }
        else if (strategy == PourStrategy.HotWeatherProtected)
        {
            // Sunshades and cooled mix lower the surface/placement temperature (ACI 305R).
            // Floor the cooler target at 68°F only when ambient is already that warm.
            var cooled = ambientTempF - 8;
            effectiveCureTempF = Math.Clamp(cooled, Math.Min(68, ambientTempF), ambientTempF);
            evaporation *= 0.40;
        }

        var maturityFactor = MaturityFactor(effectiveCureTempF);

        var strength7 = StrengthAtCalendarAge(targetPsi, 7, cementType, curing, maturityFactor);
        var strength28 = StrengthAtCalendarAge(targetPsi, 28, cementType, curing, maturityFactor);

        return new PourPrediction(
            Strength7dPsi: Math.Round(strength7),
            Strength28dPsi: Math.Round(strength28),
            SlumpInches: SlumpFor(strategy),
            CureDaysToStripping: DaysToStripping(targetPsi, cementType, curing, maturityFactor),
            ColdJointRiskPercent: Math.Round(
                ColdJointRisk(strategy, evaporation, coldProtectionRequired), 1),
            EffectiveCureTempF: Math.Round(effectiveCureTempF, 1),
            ColdProtectionRequired: coldProtectionRequired,
            HotWeatherProtectionRequired: hotWeatherProtectionRequired,
            EvaporationRateLbPerSqFtPerHour: Math.Round(evaporation, 3));
    }

    /// <summary>ACI 305R planning trigger: hot ambient or high evaporation.</summary>
    public static bool RequiresHotWeatherProtection(double ambientTempF, double evaporationRate) =>
        ambientTempF >= 80 || evaporationRate >= HighEvaporationThreshold;

    /// <summary>
    /// Nurse-Saul style temperature scaling relative to the 68°F reference cure.
    /// Below freezing hydration effectively stops, so the factor floors low rather than zero
    /// to keep predictions falsifiable rather than degenerate.
    /// </summary>
    public static double MaturityFactor(double cureTempF)
    {
        var datum = 32.0;
        var ratio = (cureTempF - datum) / (ReferenceCureTempF - datum);
        return Math.Clamp(ratio, 0.15, 1.35);
    }

    /// <summary>
    /// Strength at a calendar age, where temperature scales <em>equivalent age</em> rather than
    /// final strength — cold delays hydration, it does not permanently cap the ultimate strength.
    /// </summary>
    public static double StrengthAtCalendarAge(
        double targetPsi,
        int calendarAgeDays,
        ConcretePhysicsModel.CementType cementType,
        ConcretePhysicsModel.CuringCondition curing,
        double maturityFactor)
    {
        var equivalentAgeDays = Math.Max(1, (int)Math.Round(calendarAgeDays * maturityFactor));
        return ConcretePhysicsModel.PredictStrengthAtAge(targetPsi, equivalentAgeDays, cementType, curing);
    }

    private static double SlumpFor(PourStrategy strategy) => strategy switch
    {
        PourStrategy.ColdWeatherProtected => 3.5,
        PourStrategy.AcceleratedHighEarly => 5.0,
        PourStrategy.HotWeatherProtected => 5.25,
        _ => 4.0
    };

    /// <summary>Calendar days until stripping strength is reached, capped at a season-long ceiling.</summary>
    public const int MaxCureDaysConsidered = 120;

    private static int DaysToStripping(
        double targetPsi,
        ConcretePhysicsModel.CementType cementType,
        ConcretePhysicsModel.CuringCondition curing,
        double maturityFactor)
    {
        var required = targetPsi * StrippingStrengthFraction;
        for (var day = 1; day <= MaxCureDaysConsidered; day++)
        {
            if (StrengthAtCalendarAge(targetPsi, day, cementType, curing, maturityFactor) >= required)
            {
                return day;
            }
        }

        return MaxCureDaysConsidered;
    }

    private static double ColdJointRisk(
        PourStrategy strategy,
        double evaporationRate,
        bool coldProtectionRequired)
    {
        // Surface moisture loss drives plastic-shrinkage joints (ACI 305R). Saturating so the
        // term stays bounded but strictly increasing across the model's wide output range.
        var evaporationTerm = 20.0 * (evaporationRate / (evaporationRate + 0.5));
        var risk = 4.0 + evaporationTerm;

        if (coldProtectionRequired)
        {
            // Slow set widens the window where a delayed truck creates a joint.
            risk += strategy == PourStrategy.ColdWeatherProtected ? 1.5 : 6.0;
        }

        if (strategy == PourStrategy.AcceleratedHighEarly)
        {
            // Fast set shortens the safe placement window between loads.
            risk += 5.0;
        }

        if (strategy == PourStrategy.HotWeatherProtected)
        {
            // Fogging and shades cut the plastic-shrinkage window (ACI 305R).
            risk -= 8.0;
        }

        return Math.Clamp(risk, 2.0, 45.0);
    }
}
