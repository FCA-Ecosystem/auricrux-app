using Auricrux.Web.Services.Breakthrough.Physics;

namespace Auricrux.Web.Services.PhaseI;

/// <summary>
/// Where a MAE number came from. Fixture evaluations are reproducible in-process.
/// Field MAE requires site instruments and remains unproven until those exist.
/// </summary>
public enum CalibrationSource
{
    Fixture,
    Field
}

/// <summary>
/// Same incomplete/complete steel deflection contract as the ecosystem intelligence package.
/// Fixture MAE is defined only when a measured deflection is supplied.
/// </summary>
public static class SteelDeflectionPrior
{
    public const double DefaultAllowableRatio = 360;

    public sealed record SteelPriorResult(
        bool Incomplete,
        string? IncompleteReason,
        double? DeflectionInches,
        double? AllowableDeflectionInches,
        bool? DeflectionOk);

    public sealed record SteelTransferEval(
        bool Incomplete,
        string? IncompleteReason,
        CalibrationSource Source,
        double? PredictedDeflectionInches,
        double? MeasuredDeflectionInches,
        double? MaeInches);

    public static SteelPriorResult Predict(Dictionary<string, object>? constraints)
    {
        var missing = RequiredPhysicsInputs.Missing(constraints, RequiredPhysicsInputs.SteelDeflectionRequired);
        if (missing.Count > 0)
        {
            return new SteelPriorResult(
                Incomplete: true,
                IncompleteReason: $"Prior is incomplete. Missing: {string.Join(", ", missing)}.",
                DeflectionInches: null,
                AllowableDeflectionInches: null,
                DeflectionOk: null);
        }

        RequiredPhysicsInputs.TryRead(constraints, "span_ft", out var span);
        RequiredPhysicsInputs.TryRead(constraints, "uniform_load_plf", out var load);
        RequiredPhysicsInputs.TryRead(constraints, "moment_of_inertia_in4", out var inertia);
        var ePsi = RequiredPhysicsInputs.TryRead(constraints, "steel_E_psi", out var e)
            ? e
            : RequiredPhysicsInputs.SteelEPsiDefault;
        var ratio = RequiredPhysicsInputs.TryRead(constraints, "allowable_deflection_ratio", out var r)
            ? r
            : DefaultAllowableRatio;

        var deflection = StructuralPhysicsModel.BeamDeflectionUniformLoad(load, span, ePsi, inertia);
        var allowable = (span * 12) / ratio;
        var ok = StructuralPhysicsModel.IsDeflectionAcceptable(deflection, span, ratio);

        return new SteelPriorResult(
            Incomplete: false,
            IncompleteReason: null,
            DeflectionInches: Math.Round(deflection, 4),
            AllowableDeflectionInches: Math.Round(allowable, 4),
            DeflectionOk: ok);
    }

    public static SteelTransferEval EvaluateTransfer(
        Dictionary<string, object>? constraints,
        double? measuredDeflectionInches,
        CalibrationSource source = CalibrationSource.Fixture)
    {
        var prior = Predict(constraints);
        if (prior.Incomplete)
        {
            return new SteelTransferEval(
                Incomplete: true,
                IncompleteReason: prior.IncompleteReason,
                Source: source,
                PredictedDeflectionInches: null,
                MeasuredDeflectionInches: measuredDeflectionInches,
                MaeInches: null);
        }

        if (measuredDeflectionInches is null)
        {
            return new SteelTransferEval(
                Incomplete: true,
                IncompleteReason: source == CalibrationSource.Field
                    ? "Field MAE is unproven: no measured deflection from site instruments."
                    : "Transfer is incomplete: no measured deflection on this fixture.",
                Source: source,
                PredictedDeflectionInches: prior.DeflectionInches,
                MeasuredDeflectionInches: null,
                MaeInches: null);
        }

        var mae = Math.Abs((prior.DeflectionInches ?? 0) - measuredDeflectionInches.Value);
        return new SteelTransferEval(
            Incomplete: false,
            IncompleteReason: null,
            Source: source,
            PredictedDeflectionInches: prior.DeflectionInches,
            MeasuredDeflectionInches: measuredDeflectionInches,
            MaeInches: Math.Round(mae, 4));
    }
}
