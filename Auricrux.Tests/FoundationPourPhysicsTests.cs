using Auricrux.Web.Services.Breakthrough;
using Auricrux.Web.Services.Breakthrough.Physics;
using Xunit;

namespace Auricrux.Tests;

/// <summary>
/// Guards the calibration bucket parser. The original implementation produced
/// "0.6.0.7" and threw once enough verifications existed to populate calibration.
/// </summary>
public sealed class MetaLearningCalibrationTests
{
    [Theory]
    [InlineData("confidence_0.6_to_0.7", 0.6)]
    [InlineData("confidence_0.9_to_1.0", 0.9)]
    public void ParsesStatedConfidenceFromBucketKey(string key, double expected)
    {
        Assert.True(MetaLearningService.TryParseCalibrationLowerBound(key, out var lowerBound));
        Assert.Equal(expected, lowerBound, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-bucket")]
    public void ReturnsFalseForUnexpectedKeys(string key)
    {
        Assert.False(MetaLearningService.TryParseCalibrationLowerBound(key, out _));
    }

    [Fact]
    public void EmpiricalCalibration_OmitsEmptyBuckets_AndAveragesMeasuredAccuracy()
    {
        var calibration = MetaLearningService.ComputeEmpiricalCalibration(
        [
            (0.92, 0.40),
            (0.95, 0.50),
            (0.65, 0.80),
            (0.68, 0.90)
        ]);

        Assert.False(calibration.ContainsKey("confidence_0.7_to_0.8"));
        Assert.Equal(0.45, calibration["confidence_0.9_to_1.0"], 3);
        Assert.Equal(0.85, calibration["confidence_0.6_to_0.7"], 3);
    }

    [Fact]
    public void EmpiricalCalibration_DoesNotInventPlaceholderBuckets()
    {
        var empty = MetaLearningService.ComputeEmpiricalCalibration([]);
        Assert.Empty(empty);
    }
}

/// <summary>
/// Proves pour predictions come from ACI-based physics, not fixed ratios:
/// cold ambient must slow strength gain and push stripping later.
/// </summary>
public sealed class FoundationPourPhysicsTests
{
    [Fact]
    public void ColdAmbient_LowersEarlyStrength_AndDelaysStripping()
    {
        var warm = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.StandardAmbient,
            targetPsi: 4000,
            ambientTempF: 70,
            slabThicknessIn: 8);

        var cold = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.StandardAmbient,
            targetPsi: 4000,
            ambientTempF: 38,
            slabThicknessIn: 8);

        Assert.True(cold.Strength7dPsi < warm.Strength7dPsi);
        Assert.True(cold.CureDaysToStripping > warm.CureDaysToStripping);
        Assert.True(cold.ColdProtectionRequired);
        Assert.False(warm.ColdProtectionRequired);
    }

    [Fact]
    public void ColdWeatherProtection_RecoversCureTemperature()
    {
        var unprotected = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.StandardAmbient,
            targetPsi: 4000,
            ambientTempF: 38,
            slabThicknessIn: 8);

        var protectedPour = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.ColdWeatherProtected,
            targetPsi: 4000,
            ambientTempF: 38,
            slabThicknessIn: 8);

        Assert.True(protectedPour.EffectiveCureTempF > unprotected.EffectiveCureTempF);
        Assert.True(protectedPour.Strength7dPsi > unprotected.Strength7dPsi);
        Assert.True(protectedPour.CureDaysToStripping <= unprotected.CureDaysToStripping);
    }

    [Fact]
    public void AcceleratedMix_StripsEarlierThanStandard_AtSameTemperature()
    {
        var standard = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.StandardAmbient,
            targetPsi: 4000,
            ambientTempF: 68,
            slabThicknessIn: 8);

        var accelerated = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.AcceleratedHighEarly,
            targetPsi: 4000,
            ambientTempF: 68,
            slabThicknessIn: 8);

        Assert.True(accelerated.CureDaysToStripping <= standard.CureDaysToStripping);
        Assert.True(accelerated.ColdJointRiskPercent > standard.ColdJointRiskPercent);
    }

    [Fact]
    public void ColdPour_StillReachesStripping_TemperatureDelaysRatherThanCaps()
    {
        var cold = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.StandardAmbient,
            targetPsi: 4000,
            ambientTempF: 30,
            slabThicknessIn: 8);

        // Temperature scales equivalent age, so a cold pour is delayed but not capped forever.
        Assert.True(cold.CureDaysToStripping < FoundationPourPhysics.MaxCureDaysConsidered);
        Assert.True(cold.CureDaysToStripping > 14);
    }

    [Fact]
    public void MaturityFactor_ScalesWithCureTemperature()
    {
        Assert.Equal(1.0, FoundationPourPhysics.MaturityFactor(FoundationPourPhysics.ReferenceCureTempF), 3);
        Assert.True(FoundationPourPhysics.MaturityFactor(40) < 1.0);
        Assert.True(FoundationPourPhysics.MaturityFactor(20) >= 0.15);
    }

    [Fact]
    public void HighEvaporation_RaisesColdJointRisk()
    {
        var calm = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.StandardAmbient,
            targetPsi: 4000,
            ambientTempF: 75,
            slabThicknessIn: 8,
            relativeHumidity: 0.80,
            windSpeedMph: 2);

        var windyDry = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.StandardAmbient,
            targetPsi: 4000,
            ambientTempF: 95,
            slabThicknessIn: 8,
            relativeHumidity: 0.15,
            windSpeedMph: 20);

        Assert.True(windyDry.EvaporationRateLbPerSqFtPerHour > calm.EvaporationRateLbPerSqFtPerHour);
        Assert.True(windyDry.ColdJointRiskPercent > calm.ColdJointRiskPercent);
        Assert.True(windyDry.HotWeatherProtectionRequired);
    }

    [Fact]
    public void HotWeatherProtection_CutsEvaporation_AndLowersJointRisk()
    {
        var unprotected = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.StandardAmbient,
            targetPsi: 4000,
            ambientTempF: 95,
            slabThicknessIn: 8,
            relativeHumidity: 0.15,
            windSpeedMph: 20);

        var protectedPour = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.HotWeatherProtected,
            targetPsi: 4000,
            ambientTempF: 95,
            slabThicknessIn: 8,
            relativeHumidity: 0.15,
            windSpeedMph: 20);

        Assert.True(protectedPour.EvaporationRateLbPerSqFtPerHour < unprotected.EvaporationRateLbPerSqFtPerHour);
        Assert.True(protectedPour.ColdJointRiskPercent < unprotected.ColdJointRiskPercent);
        Assert.True(protectedPour.EffectiveCureTempF < unprotected.EffectiveCureTempF);
        Assert.True(unprotected.HotWeatherProtectionRequired);
    }

    [Fact]
    public void HotWeatherProtection_DoesNotThrow_WhenAmbientIsAlreadyCool()
    {
        var cool = FoundationPourPhysics.Predict(
            FoundationPourPhysics.PourStrategy.HotWeatherProtected,
            targetPsi: 4000,
            ambientTempF: 42,
            slabThicknessIn: 8);

        Assert.Equal(42, cool.EffectiveCureTempF, 1);
    }
}

public sealed class FoundationAndStructuralPhysicsTests
{
    [Fact]
    public void LongerPiles_IncreaseMeyerhofCapacity()
    {
        var shortPile = FoundationPhysicsModel.DrivenPileCapacity(12, 20, 800, 8000);
        var longPile = FoundationPhysicsModel.DrivenPileCapacity(12, 80, 800, 8000);
        Assert.True(longPile > shortPile);
    }

    [Fact]
    public void WiderFooting_IncreasesAllowableBearingContributionFromNgamma()
    {
        var narrow = FoundationPhysicsModel.AllowableBearingCapacity(
            FoundationPhysicsModel.FootingShape.Square, 4, 4, 200, 30, 120);
        var wide = FoundationPhysicsModel.AllowableBearingCapacity(
            FoundationPhysicsModel.FootingShape.Square, 12, 4, 200, 30, 120);
        Assert.True(wide > narrow);
    }

    [Fact]
    public void LongSpan_FailsL360_WhileShortSpanPasses()
    {
        const double e = 29_000_000;
        const double i = 475;
        var shortDefl = StructuralPhysicsModel.BeamDeflectionUniformLoad(400, 30, e, i);
        var longDefl = StructuralPhysicsModel.BeamDeflectionUniformLoad(400, 80, e, i);
        Assert.True(StructuralPhysicsModel.IsDeflectionAcceptable(shortDefl, 30));
        Assert.False(StructuralPhysicsModel.IsDeflectionAcceptable(longDefl, 80));
    }
}
