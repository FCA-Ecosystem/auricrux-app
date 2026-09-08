using Auricrux.Web.Services.PhaseI;
using Xunit;

namespace Auricrux.Tests;

public sealed class SteelDeflectionPriorTests
{
    [Fact]
    public void SteelTransfer_FixtureMeasurement_DefinesMae_FieldWithoutMeasure_IsIncomplete()
    {
        var constraints = new Dictionary<string, object>
        {
            ["span_ft"] = 30,
            ["uniform_load_plf"] = 400,
            ["moment_of_inertia_in4"] = 475
        };
        var prior = SteelDeflectionPrior.Predict(constraints);
        Assert.False(prior.Incomplete);
        Assert.True(prior.DeflectionInches > 0);

        var fixture = SteelDeflectionPrior.EvaluateTransfer(
            constraints,
            measuredDeflectionInches: prior.DeflectionInches + 0.1,
            CalibrationSource.Fixture);
        Assert.False(fixture.Incomplete);
        Assert.Equal(CalibrationSource.Fixture, fixture.Source);
        Assert.Equal(0.1, fixture.MaeInches!.Value, 3);

        var field = SteelDeflectionPrior.EvaluateTransfer(constraints, measuredDeflectionInches: null, CalibrationSource.Field);
        Assert.True(field.Incomplete);
        Assert.Equal(CalibrationSource.Field, field.Source);
        Assert.Contains("Field MAE", field.IncompleteReason, StringComparison.Ordinal);
        Assert.Null(field.MaeInches);
    }

    [Fact]
    public void SteelPrior_MissingSpan_IsIncomplete()
    {
        var prior = SteelDeflectionPrior.Predict(new Dictionary<string, object>
        {
            ["uniform_load_plf"] = 400,
            ["moment_of_inertia_in4"] = 475
        });
        Assert.True(prior.Incomplete);
        Assert.Contains("span_ft", prior.IncompleteReason, StringComparison.Ordinal);
    }
}
