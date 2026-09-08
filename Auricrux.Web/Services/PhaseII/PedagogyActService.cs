using Auricrux.Web.Services.Breakthrough;
using Auricrux.Web.Services.PhaseI;

namespace Auricrux.Web.Services.PhaseII;

/// <summary>
/// Proof-gated pour/steel act on the live NSF host. Records an audit in process memory.
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
        _loop.AddPedagogyAct(new PedagogyActRecord
        {
            ActId = id,
            ProjectId = request.ProjectId ?? "",
            Slice = slice,
            Action = action,
            DecisionId = request.DecisionId ?? "",
            VerificationId = request.VerificationId ?? "",
            Accepted = true,
            MutationApplied = false,
            Reason = "Proof-gated pedagogy act accepted. Process-memory audit recorded. No PM/finance table mutation.",
            CatalogMatchIsNotSynthesis = true
        });

        return new PedagogyActResult(
            Accepted: true,
            MutationApplied: false,
            ActId: id,
            Slice: slice,
            Action: action,
            GovernanceClass: "safety",
            Reason: "Proof-gated pedagogy act accepted. Process-memory audit recorded. No PM/finance table mutation.",
            CatalogMatchIsNotSynthesis: true);
    }

    private static PedagogyActResult Refuse(string reason, string slice, string action) =>
        new(
            Accepted: false,
            MutationApplied: false,
            ActId: null,
            Slice: slice,
            Action: action,
            GovernanceClass: "safety",
            Reason: reason,
            CatalogMatchIsNotSynthesis: true);
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
    bool CatalogMatchIsNotSynthesis);
