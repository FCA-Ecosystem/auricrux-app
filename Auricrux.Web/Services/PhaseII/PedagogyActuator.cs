namespace Auricrux.Web.Services.PhaseII;

/// <summary>
/// NSF Phase II: pedagogy-as-actuator. A verified pour/steel loop may propose a
/// governed field lesson and hold/proceed act. Catalog matching is not lesson synthesis.
/// Incomplete priors and open loops silence — they do not teach or mutate.
/// A matching prior job lesson confirms the same act (Improve) without unique synthesis.
/// </summary>
public static class PedagogyActuator
{
    public const string PourSlice = "foundation-pour";
    public const string SteelSlice = "steel";

    public sealed record PedagogyProposal(
        bool Silence,
        string? SilenceReason,
        string Slice,
        string? ProposedAction,
        string? FieldLessonTopic,
        string? FieldLesson,
        string GovernanceClass,
        bool CatalogMatchIsNotSynthesis,
        bool PriorLessonConfirmed,
        string? PriorProposedAction);

    public static PedagogyProposal FromPourLoop(
        bool incomplete,
        string? incompleteReason,
        bool loopClosed,
        bool requiresCorrection,
        string recommendedApproach,
        string? priorProposedAction = null)
    {
        if (incomplete)
            return Silenced(PourSlice, incompleteReason ?? "Prior is incomplete. Pedagogy cannot actuate.");
        if (!loopClosed)
            return Silenced(PourSlice, "Loop is not closed. Pedagogy cannot actuate on an unfalsified hypothesis.");

        var action = requiresCorrection ? "hold-strip" : "proceed-strip";
        var topic = requiresCorrection
            ? "Hold stripping until cylinders catch the physics"
            : $"Proceed under {recommendedApproach}";
        var lesson = requiresCorrection
            ? $"Job-derived lesson (not catalog matching): field cylinders diverged from '{recommendedApproach}'. Hold stripping until the equivalent-age prior and breaks agree. Human accept is required before any schedule mutation."
            : $"Job-derived lesson (not catalog matching): '{recommendedApproach}' closed the loop closely enough to propose proceed-strip. Human accept is still required before mutation.";
        return Closed(PourSlice, action, topic, lesson, priorProposedAction);
    }

    public static PedagogyProposal FromSteelLoop(
        bool incomplete,
        string? incompleteReason,
        bool loopClosed,
        bool requiresCorrection,
        string recommendedApproach,
        string? priorProposedAction = null)
    {
        if (incomplete)
            return Silenced(SteelSlice, incompleteReason ?? "Prior is incomplete. Pedagogy cannot actuate.");
        if (!loopClosed)
            return Silenced(SteelSlice, "Loop is not closed. Pedagogy cannot actuate on an unfalsified hypothesis.");

        var action = requiresCorrection ? "hold-erection" : "proceed-erection";
        var topic = requiresCorrection
            ? "Hold erection until deflection catches L/360"
            : $"Proceed under {recommendedApproach}";
        var lesson = requiresCorrection
            ? $"Job-derived lesson (not catalog matching): field deflection diverged from '{recommendedApproach}'. Hold erection until the steel prior and L/360 agree. Human accept is required before any schedule mutation."
            : $"Job-derived lesson (not catalog matching): '{recommendedApproach}' closed the loop closely enough to propose proceed-erection. Human accept is still required before mutation.";
        return Closed(SteelSlice, action, topic, lesson, priorProposedAction);
    }

    private static PedagogyProposal Silenced(string slice, string reason) =>
        new(
            Silence: true,
            SilenceReason: reason,
            Slice: slice,
            ProposedAction: null,
            FieldLessonTopic: null,
            FieldLesson: null,
            GovernanceClass: "safety",
            CatalogMatchIsNotSynthesis: true,
            PriorLessonConfirmed: false,
            PriorProposedAction: null);

    private static PedagogyProposal Closed(
        string slice,
        string action,
        string topic,
        string lesson,
        string? priorProposedAction)
    {
        var confirmed = !string.IsNullOrWhiteSpace(priorProposedAction)
                        && string.Equals(priorProposedAction, action, StringComparison.OrdinalIgnoreCase);
        if (confirmed)
        {
            topic = "Prior job lesson confirmed: " + topic;
            lesson += " A previous job-derived lesson on this project proposed the same act; this closed loop confirmed it. Still not unique synthesis.";
        }

        return new PedagogyProposal(
            Silence: false,
            SilenceReason: null,
            Slice: slice,
            ProposedAction: action,
            FieldLessonTopic: topic,
            FieldLesson: lesson,
            GovernanceClass: "safety",
            CatalogMatchIsNotSynthesis: true,
            PriorLessonConfirmed: confirmed,
            PriorProposedAction: priorProposedAction);
    }
}
