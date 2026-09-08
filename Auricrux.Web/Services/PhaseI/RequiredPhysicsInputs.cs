namespace Auricrux.Web.Services.PhaseI;

/// <summary>
/// Required inputs for a first-principles prior. Missing keys leave the prior incomplete;
/// the runtime must not impute a fluent psi, day count, or deflection.
/// Steel E may use the AISC 29,000 ksi constitutive constant; span, load, and I may not.
/// </summary>
public static class RequiredPhysicsInputs
{
    public static readonly string[] PourRequired =
        ["target_psi", "ambient_temp_f", "slab_thickness_in"];

    public static readonly string[] SteelDeflectionRequired =
        ["span_ft", "uniform_load_plf", "moment_of_inertia_in4"];

    public const double SteelEPsiDefault = 29_000_000;

    public static bool IsPourDecision(string? phase, string? context)
    {
        var hay = $"{phase} {context}";
        return hay.Contains("pour", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSteelDeflectionDecision(string? phase, string? context)
    {
        var hay = $"{phase} {context}";
        return hay.Contains("steel", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("structural", StringComparison.OrdinalIgnoreCase)
               || hay.Contains("deflection", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryRead(Dictionary<string, object>? constraints, string key, out double value)
    {
        value = 0;
        if (constraints is null)
            return false;
        foreach (var existing in constraints)
        {
            if (!existing.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            return double.TryParse(existing.Value?.ToString(), out value);
        }
        return false;
    }

    public static List<string> Missing(Dictionary<string, object>? constraints, IReadOnlyList<string> required)
    {
        var missing = new List<string>();
        foreach (var key in required)
        {
            if (!TryRead(constraints, key, out _))
                missing.Add(key);
        }
        return missing;
    }

    public static string? IncompleteReason(
        string? phase,
        string? context,
        Dictionary<string, object>? constraints,
        int hypothesisCount)
    {
        if (hypothesisCount > 0)
            return null;
        if (IsPourDecision(phase, context))
        {
            var missing = Missing(constraints, PourRequired);
            return missing.Count == 0
                ? "Prior is incomplete: required pour inputs are absent."
                : $"Prior is incomplete. Missing: {string.Join(", ", missing)}.";
        }
        if (IsSteelDeflectionDecision(phase, context))
        {
            var missing = Missing(constraints, SteelDeflectionRequired);
            return missing.Count == 0
                ? "Prior is incomplete: required steel deflection inputs are absent."
                : $"Prior is incomplete. Missing: {string.Join(", ", missing)}.";
        }
        return "Prior is incomplete: required inputs are absent.";
    }
}
