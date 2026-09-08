namespace Auricrux.Web.Services.PhaseII;

/// <summary>
/// NSF Phase II: pedagogy-as-actuator. A verified pour/steel loop may propose a
/// governed field lesson and hold/proceed act. Catalog matching is not lesson synthesis.
/// Incomplete priors and open loops silence — they do not teach or mutate.
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
        bool CatalogMatchIsNotSynthesis);

    public static PedagogyProposal FromPourLoop(
        bool incomplete,
        string? incompleteReason,
        bool loopClosed,
        bool requiresCorrection,
        string recommendedApproach)
    {
        if (incomplete)
        {
            return new PedagogyProposal(
                Silence: true,
                SilenceReason: incompleteReason ?? "Prior is incomplete. Pedagogy cannot actuate.",
                Slice: PourSlice,
                ProposedAction: null,
                FieldLessonTopic: null,
                FieldLesson: null,
                GovernanceClass: "safety",
                CatalogMatchIsNotSynthesis: true);
        }

        if (!loopClosed)
        {
            return new PedagogyProposal(
                Silence: true,
                SilenceReason: "Loop is not closed. Pedagogy cannot actuate on an unfalsified hypothesis.",
                Slice: PourSlice,
                ProposedAction: null,
                FieldLessonTopic: null,
                FieldLesson: null,
                GovernanceClass: "safety",
                CatalogMatchIsNotSynthesis: true);
        }

        var action = requiresCorrection ? "hold-strip" : "proceed-strip";
        var topic = requiresCorrection
            ? "Hold stripping until cylinders catch the physics"
            : $"Proceed under {recommendedApproach}";
        var lesson = requiresCorrection
            ? $"Job-derived lesson (not catalog matching): field cylinders diverged from '{recommendedApproach}'. Hold stripping until the equivalent-age prior and breaks agree. Human accept is required before any schedule mutation."
            : $"Job-derived lesson (not catalog matching): '{recommendedApproach}' closed the loop closely enough to propose proceed-strip. Human accept is still required before mutation.";

        return new PedagogyProposal(
            Silence: false,
            SilenceReason: null,
            Slice: PourSlice,
            ProposedAction: action,
            FieldLessonTopic: topic,
            FieldLesson: lesson,
            GovernanceClass: "safety",
            CatalogMatchIsNotSynthesis: true);
    }
}
