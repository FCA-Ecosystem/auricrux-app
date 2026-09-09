using Auricrux.Web.Services.Breakthrough;
using Auricrux.Web.Services.PhaseI;

namespace Auricrux.Web.Services.PhaseII;

/// <summary>
/// Proof-gated pour/steel act on the live NSF host.
/// hold-strip / proceed-strip mutate Auricrux pour-control process memory only.
/// hold-erection / proceed-erection mutate Auricrux erection-control process memory only.
/// Never mutates PM schedule or finance tables.
/// </summary>
public sealed class PedagogyActService
{
    public static readonly HashSet<string> AllowedSlices = new(StringComparer.OrdinalIgnoreCase)
    {
        "foundation-pour",
        "stripping",
        "steel",
        "steel-deflection"
    };

    public static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "hold-strip",
        "proceed-strip",
        "hold-erection",
        "proceed-erection",
        "hold",
        "proceed",
        "record-field-lesson",
        "record-intent"
    };

    private readonly BreakthroughLoopStore _loop;

    public PedagogyActService(BreakthroughLoopStore loop) => _loop = loop;

    public IReadOnlyList<PedagogyActRecord> List(string? projectId, int limit = 20) =>
        _loop.ListPedagogyActs(projectId, limit);

    public PourControlRecord? GetPourControl(string? projectId) =>
        _loop.GetPourControl(projectId);

    public ErectionControlRecord? GetErectionControl(string? projectId) =>
        _loop.GetErectionControl(projectId);

    public PedagogyActResult Execute(PedagogyActRequest request)
    {
        var slice = (request.Slice ?? "").Trim();
        var action = (request.Action ?? "").Trim();
        if (!AllowedSlices.Contains(slice))
            return Refuse($"Slice '{slice}' is not a proof-gated pour/steel act.", slice, action);
        if (!AllowedActions.Contains(action))
            return Refuse(
                $"Action '{action}' is not authorized for '{slice}'. Allowed: hold-strip, proceed-strip, hold-erection, proceed-erection, record-field-lesson, record-intent",
                slice,
                action);

        var packet = EvidenceProofGate.FromAct(
            request.DecisionId,
            request.VerificationId,
            request.HumanAccepted,
            request.OverrideAudit,
            request.EvidenceJson);
        var gate = EvidenceProofGate.Evaluate(packet);
        if (!gate.AllowProceed)
            return Refuse(gate.Reason, slice, action);

        var id = Guid.NewGuid().ToString("n");
        var projectId = string.IsNullOrWhiteSpace(request.ProjectId)
            ? (IsSteelSlice(slice) ? BreakthroughLoopStore.DefaultSteelProjectId : BreakthroughLoopStore.DefaultPourProjectId)
            : request.ProjectId.Trim();
        PourControlRecord? pour = null;
        ErectionControlRecord? erection = null;
        var pourMutated = false;
        var erectionMutated = false;
        if (IsHoldStrip(action, slice))
        {
            pour = _loop.HoldStrip(projectId, id);
            pourMutated = true;
        }
        else if (IsProceedStrip(action, slice))
        {
            pour = _loop.ProceedStrip(projectId, id);
            pourMutated = true;
        }
        else if (IsHoldErection(action, slice))
        {
            erection = _loop.HoldErection(projectId, id);
            erectionMutated = true;
        }
        else if (IsProceedErection(action, slice))
        {
            erection = _loop.ProceedErection(projectId, id);
            erectionMutated = true;
        }

        var mutated = pourMutated || erectionMutated;
        var target = pourMutated
            ? BreakthroughLoopStore.PourControlMutationTarget
            : erectionMutated
                ? BreakthroughLoopStore.ErectionControlMutationTarget
                : null;
        var reason = mutated
            ? $"Proof-gated pedagogy act accepted. Auricrux {target} updated. No PM/finance table mutation."
            : "Proof-gated pedagogy act accepted. Process-memory audit recorded. No PM/finance table mutation.";

        _loop.AddPedagogyAct(new PedagogyActRecord
        {
            ActId = id,
            ProjectId = projectId,
            Slice = slice,
            Action = action,
            DecisionId = request.DecisionId ?? "",
            VerificationId = request.VerificationId ?? "",
            Accepted = true,
            MutationApplied = mutated,
            Reason = reason,
            CatalogMatchIsNotSynthesis = true
        });

        return new PedagogyActResult(
            Accepted: true,
            MutationApplied: mutated,
            ActId: id,
            Slice: slice,
            Action: action,
            GovernanceClass: "safety",
            Reason: reason,
            CatalogMatchIsNotSynthesis: true,
            PourControlMutated: pourMutated,
            ErectionControlMutated: erectionMutated,
            MutationTarget: target,
            PmOrFinanceMutated: false,
            BaselineStripDays: pour?.BaselineStripDays,
            CurrentStripDays: pour?.CurrentStripDays,
            HoldActive: pour?.HoldActive ?? erection?.HoldActive,
            PlannedStripAtUtc: pour?.PlannedStripAtUtc,
            CurrentStripAtUtc: pour?.CurrentStripAtUtc,
            BaselineErectionDays: erection?.BaselineErectionDays,
            CurrentErectionDays: erection?.CurrentErectionDays,
            PlannedErectionAtUtc: erection?.PlannedErectionAtUtc,
            CurrentErectionAtUtc: erection?.CurrentErectionAtUtc);
    }

    private static bool IsHoldStrip(string action, string slice) =>
        action.Equals("hold-strip", StringComparison.OrdinalIgnoreCase)
        || (action.Equals("hold", StringComparison.OrdinalIgnoreCase) && IsPourSlice(slice));

    private static bool IsProceedStrip(string action, string slice) =>
        action.Equals("proceed-strip", StringComparison.OrdinalIgnoreCase)
        || (action.Equals("proceed", StringComparison.OrdinalIgnoreCase) && IsPourSlice(slice));

    private static bool IsHoldErection(string action, string slice) =>
        action.Equals("hold-erection", StringComparison.OrdinalIgnoreCase)
        || (action.Equals("hold", StringComparison.OrdinalIgnoreCase) && IsSteelSlice(slice));

    private static bool IsProceedErection(string action, string slice) =>
        action.Equals("proceed-erection", StringComparison.OrdinalIgnoreCase)
        || (action.Equals("proceed", StringComparison.OrdinalIgnoreCase) && IsSteelSlice(slice));

    private static bool IsPourSlice(string slice) =>
        slice.Equals("foundation-pour", StringComparison.OrdinalIgnoreCase)
        || slice.Equals("stripping", StringComparison.OrdinalIgnoreCase);

    private static bool IsSteelSlice(string slice) =>
        slice.Equals("steel", StringComparison.OrdinalIgnoreCase)
        || slice.Equals("steel-deflection", StringComparison.OrdinalIgnoreCase);

    private static PedagogyActResult Refuse(string reason, string slice, string action) =>
        new(
            Accepted: false,
            MutationApplied: false,
            ActId: null,
            Slice: slice,
            Action: action,
            GovernanceClass: "safety",
            Reason: reason,
            CatalogMatchIsNotSynthesis: true,
            PourControlMutated: false,
            MutationTarget: null,
            PmOrFinanceMutated: false);
}

public sealed class PedagogyActRequest
{
    public string? Action { get; init; }
    public string? Slice { get; init; }
    public string? ProjectId { get; init; }
    public string? DecisionId { get; init; }
    public string? VerificationId { get; init; }
    public bool HumanAccepted { get; init; }
    public bool OverrideAudit { get; init; }
    public string? EvidenceJson { get; init; }
}

public sealed record PedagogyActResult(
    bool Accepted,
    bool MutationApplied,
    string? ActId,
    string Slice,
    string Action,
    string GovernanceClass,
    string Reason,
    bool CatalogMatchIsNotSynthesis,
    bool PourControlMutated = false,
    bool ErectionControlMutated = false,
    string? MutationTarget = null,
    bool PmOrFinanceMutated = false,
    int? BaselineStripDays = null,
    int? CurrentStripDays = null,
    bool? HoldActive = null,
    DateTime? PlannedStripAtUtc = null,
    DateTime? CurrentStripAtUtc = null,
    int? BaselineErectionDays = null,
    int? CurrentErectionDays = null,
    DateTime? PlannedErectionAtUtc = null,
    DateTime? CurrentErectionAtUtc = null);
