using Auricrux.Web.Services.Breakthrough;
using Auricrux.Web.Services.PhaseI;

namespace Auricrux.Web.Services.PhaseII;

/// <summary>
/// Proof-gated pour/steel act on the live NSF host.
/// hold-strip / proceed-strip mutate Auricrux pour-control process memory only.
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
            ? BreakthroughLoopStore.DefaultPourProjectId
            : request.ProjectId.Trim();
        PourControlRecord? control = null;
        var pourMutated = false;
        if (IsHoldStrip(action, slice))
        {
            control = _loop.HoldStrip(projectId, id);
            pourMutated = true;
        }
        else if (IsProceedStrip(action, slice))
        {
            control = _loop.ProceedStrip(projectId, id);
            pourMutated = true;
        }

        var reason = pourMutated
            ? "Proof-gated pedagogy act accepted. Auricrux pour-control stripping date updated in process memory. No PM/finance table mutation."
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
            MutationApplied = pourMutated,
            Reason = reason,
            CatalogMatchIsNotSynthesis = true
        });

        return new PedagogyActResult(
            Accepted: true,
            MutationApplied: pourMutated,
            ActId: id,
            Slice: slice,
            Action: action,
            GovernanceClass: "safety",
            Reason: reason,
            CatalogMatchIsNotSynthesis: true,
            PourControlMutated: pourMutated,
            MutationTarget: pourMutated ? BreakthroughLoopStore.PourControlMutationTarget : null,
            PmOrFinanceMutated: false,
            BaselineStripDays: control?.BaselineStripDays,
            CurrentStripDays: control?.CurrentStripDays,
            HoldActive: control?.HoldActive,
            PlannedStripAtUtc: control?.PlannedStripAtUtc,
            CurrentStripAtUtc: control?.CurrentStripAtUtc);
    }

    private static bool IsHoldStrip(string action, string slice) =>
        action.Equals("hold-strip", StringComparison.OrdinalIgnoreCase)
        || (action.Equals("hold", StringComparison.OrdinalIgnoreCase) && IsPourSlice(slice));

    private static bool IsProceedStrip(string action, string slice) =>
        action.Equals("proceed-strip", StringComparison.OrdinalIgnoreCase)
        || (action.Equals("proceed", StringComparison.OrdinalIgnoreCase) && IsPourSlice(slice));

    private static bool IsPourSlice(string slice) =>
        slice.Equals("foundation-pour", StringComparison.OrdinalIgnoreCase)
        || slice.Equals("stripping", StringComparison.OrdinalIgnoreCase);

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
    string? MutationTarget = null,
    bool PmOrFinanceMutated = false,
    int? BaselineStripDays = null,
    int? CurrentStripDays = null,
    bool? HoldActive = null,
    DateTime? PlannedStripAtUtc = null,
    DateTime? CurrentStripAtUtc = null);
