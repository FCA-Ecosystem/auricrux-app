using System.Text.Json;

namespace Auricrux.Web.Services.PhaseI;

public enum EvidenceFault
{
    None,
    MissingEvidence,
    StaleTimestamp,
    ContradictoryReadings,
    SpoofedOrUnsigned,
    UnauthorizedActor,
    HumanAcceptedFalse
}

/// <summary>
/// Aim 2: proof-gated cognition. Incomplete evidence cannot emit a complete
/// pour/steel decision. Silence is the correct failure.
/// Always-answer control disables the gate so false-complete rate can be measured.
/// </summary>
public static class EvidenceProofGate
{
    public sealed record EvidencePacket(
        bool HasEvidence,
        DateTimeOffset? EvidenceTimestamp,
        TimeSpan? MaxAge,
        bool Contradictory,
        bool Signed,
        bool ActorAuthorized,
        bool HumanAccepted,
        bool OverrideAudit,
        string? DecisionId,
        string? VerificationId);

    public sealed record ProofGateDecision(
        bool AllowProceed,
        bool Incomplete,
        bool Silence,
        EvidenceFault Fault,
        string Reason);

    public static ProofGateDecision Evaluate(EvidencePacket packet, bool gateDisabled = false)
    {
        if (gateDisabled)
        {
            return new ProofGateDecision(
                AllowProceed: true,
                Incomplete: false,
                Silence: false,
                Fault: EvidenceFault.None,
                Reason: "Always-answer control: proof-gate disabled.");
        }

        if (packet.OverrideAudit)
        {
            return new ProofGateDecision(
                AllowProceed: true,
                Incomplete: false,
                Silence: false,
                Fault: EvidenceFault.None,
                Reason: "Explicit override audit present.");
        }

        var fault = Classify(packet);
        if (fault == EvidenceFault.None)
        {
            return new ProofGateDecision(
                AllowProceed: true,
                Incomplete: false,
                Silence: false,
                Fault: EvidenceFault.None,
                Reason: "Evidence sufficient.");
        }

        return new ProofGateDecision(
            AllowProceed: false,
            Incomplete: true,
            Silence: true,
            Fault: fault,
            Reason: SilenceReason(fault));
    }

    public static EvidencePacket FromAct(
        string? decisionId,
        string? verificationId,
        bool humanAccepted,
        bool overrideAudit,
        string? evidenceJson)
    {
        var hasIds = !string.IsNullOrWhiteSpace(decisionId) && !string.IsNullOrWhiteSpace(verificationId);
        var flags = ParseFlags(evidenceJson);
        return new EvidencePacket(
            HasEvidence: hasIds && !flags.Missing,
            EvidenceTimestamp: flags.Timestamp,
            MaxAge: flags.MaxAge ?? TimeSpan.FromHours(24),
            Contradictory: flags.Contradictory,
            Signed: hasIds && !flags.Spoofed,
            ActorAuthorized: !flags.Unauthorized,
            HumanAccepted: humanAccepted,
            OverrideAudit: overrideAudit,
            DecisionId: decisionId,
            VerificationId: verificationId);
    }

    public static double FalseCompleteRate(
        IReadOnlyList<EvidencePacket> faultedFixtures,
        bool gateDisabled)
    {
        if (faultedFixtures.Count == 0)
            return 0;
        var completes = faultedFixtures.Count(p => Evaluate(p, gateDisabled).AllowProceed);
        return completes / (double)faultedFixtures.Count;
    }

    private static EvidenceFault Classify(EvidencePacket packet)
    {
        if (!packet.ActorAuthorized)
            return EvidenceFault.UnauthorizedActor;
        if (!packet.HumanAccepted)
            return EvidenceFault.HumanAcceptedFalse;
        if (!packet.HasEvidence
            || string.IsNullOrWhiteSpace(packet.DecisionId)
            || string.IsNullOrWhiteSpace(packet.VerificationId))
            return EvidenceFault.MissingEvidence;
        if (!packet.Signed)
            return EvidenceFault.SpoofedOrUnsigned;
        if (packet.Contradictory)
            return EvidenceFault.ContradictoryReadings;
        if (packet.EvidenceTimestamp is { } ts && packet.MaxAge is { } max
            && DateTimeOffset.UtcNow - ts > max)
            return EvidenceFault.StaleTimestamp;
        return EvidenceFault.None;
    }

    private static string SilenceReason(EvidenceFault fault) => fault switch
    {
        EvidenceFault.MissingEvidence => "Incomplete: missing decisionId+verification. Silence is the correct failure.",
        EvidenceFault.StaleTimestamp => "Incomplete: evidence timestamp is stale. Silence is the correct failure.",
        EvidenceFault.ContradictoryReadings => "Incomplete: contradictory readings. Silence is the correct failure.",
        EvidenceFault.SpoofedOrUnsigned => "Incomplete: unsigned or spoofed payload. Silence is the correct failure.",
        EvidenceFault.UnauthorizedActor => "Incomplete: unauthorized actor. Silence is the correct failure.",
        EvidenceFault.HumanAcceptedFalse => "Incomplete: humanAccepted is false. Silence is the correct failure.",
        EvidenceFault.None => "Evidence sufficient.",
        _ => throw new InvalidOperationException($"Unhandled evidence fault '{fault}'.")
    };

    private sealed record EvidenceFlags(
        bool Missing,
        bool Contradictory,
        bool Spoofed,
        bool Unauthorized,
        DateTimeOffset? Timestamp,
        TimeSpan? MaxAge);

    private static EvidenceFlags ParseFlags(string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson))
            return new EvidenceFlags(false, false, false, false, null, null);
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            var root = doc.RootElement;
            bool Flag(string name) =>
                root.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True;
            DateTimeOffset? ts = null;
            if (root.TryGetProperty("timestampUtc", out var tsEl)
                && tsEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed))
                ts = parsed;
            TimeSpan? maxAge = null;
            if (root.TryGetProperty("maxAgeHours", out var ageEl) && ageEl.TryGetDouble(out var hours))
                maxAge = TimeSpan.FromHours(hours);
            return new EvidenceFlags(
                Flag("missing"),
                Flag("contradictory"),
                Flag("spoofed") || Flag("unsigned"),
                Flag("unauthorized"),
                ts,
                maxAge);
        }
        catch (JsonException)
        {
            return new EvidenceFlags(Missing: true, false, false, false, null, null);
        }
    }
}
