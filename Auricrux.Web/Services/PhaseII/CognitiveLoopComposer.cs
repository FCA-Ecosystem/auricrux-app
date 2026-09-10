namespace Auricrux.Web.Services.PhaseII;

/// <summary>
/// One Observe → Understand → Learn → Act → Improve → Connect turn.
/// Field work becomes a lesson; the lesson applies back into Auricrux controls
/// when a human accepts. The next turn confirms the prior lesson.
/// Not unique synthesis. Not catalog actuation. Not PM/finance mutation.
/// </summary>
public static class CognitiveLoopComposer
{
    public const string GovernanceNote =
        "Field↔lesson↔system cognitive loop. Observe current field work, Learn a job-grounded lesson, Act into Auricrux controls when human-accepted, Improve on the next turn. Not unique synthesis. Not catalog actuation.";

    public sealed record Request(
        string? ApprenticeId,
        string? Role,
        string? Slice,
        string? ProjectId,
        string? FieldActivity,
        IReadOnlyList<string>? KnownGaps,
        bool HumanAccepted,
        string? DecisionId,
        string? VerificationId);

    public sealed record PriorTurn(
        string CycleId,
        string FieldActivity,
        string? ProposedAction,
        bool ActApplied,
        int CycleNumber);

    public sealed record SystemSnapshot(
        bool HoldActive,
        string? MutationTarget,
        int? CurrentControlDays,
        int? BaselineControlDays);

    public sealed record ActSnapshot(
        bool Attempted,
        bool Accepted,
        bool Applied,
        string? Action,
        string Reason,
        string? MutationTarget,
        bool? HoldActive);

    public sealed record Cycle(
        string CycleId,
        int CycleNumber,
        string ApprenticeId,
        string Role,
        string Slice,
        string? ProjectId,
        string FieldActivity,
        string Observe,
        string Understand,
        string Learn,
        string Act,
        string Improve,
        string Connect,
        string? PlanId,
        string? JobLessonId,
        string? ProposedAction,
        bool ActApplied,
        bool PriorTurnConfirmed,
        bool HoldStillInForce,
        bool UniqueSynthesis,
        bool CatalogActuated,
        bool PmOrFinanceMutated,
        DateTime CreatedAtUtc);

    public sealed record Result(
        bool Silence,
        string? SilenceReason,
        Cycle? Cycle,
        ApprenticeLessonPlanComposer.Plan? Plan,
        ActSnapshot Act,
        bool UniqueSynthesis,
        bool CatalogMatching,
        bool CatalogActuated,
        bool PmOrFinanceMutated);

    public static Result? Incomplete(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.FieldActivity))
            return Silenced("Field activity is incomplete. Auricrux will not invent what the apprentice is doing.");
        if (string.IsNullOrWhiteSpace(request.ApprenticeId))
            return Silenced("Apprentice identity is incomplete. Auricrux will not invent a learner.");
        return null;
    }

    public static bool LooksLikeFieldWork(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var q = text.ToLowerInvariant();
        return q.Contains("strip")
               || q.Contains("form")
               || q.Contains("cylinder")
               || q.Contains("pour")
               || q.Contains("steel")
               || q.Contains("erect")
               || q.Contains("deflection")
               || q.Contains("gfci")
               || q.Contains("focus four")
               || q.Contains("checking")
               || q.Contains("in the field")
               || q.Contains("on site")
               || q.Contains("on-site")
               || q.Contains("at the wall");
    }

    public static IReadOnlyList<string> InferGaps(
        string? fieldActivity,
        string? slice,
        IReadOnlyList<string>? stated)
    {
        var gaps = new List<string>();
        if (stated is not null)
        {
            gaps.AddRange(stated
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim()));
        }

        var text = fieldActivity ?? "";
        if (Contains(text, "focus four") || Contains(text, "fall hazard") || Contains(text, "falls"))
            gaps.Add("Focus Four");
        if (Contains(text, "gfci") || Contains(text, "shock") || Contains(text, "electrocution"))
            gaps.Add("GFCI");
        if (Contains(text, "steel") || Contains(text, "deflection") || Contains(text, "bolt"))
            gaps.Add("steel erection");
        if (Contains(text, "cylinder") || Contains(text, "psi") || Contains(text, "break"))
            gaps.Add("cylinder breaks");
        if (Contains(text, "strip") || Contains(text, "form"))
            gaps.Add("foundation pour stripping");

        if (gaps.Count == 0 && !string.IsNullOrWhiteSpace(slice))
        {
            gaps.Add(slice.Contains("steel", StringComparison.OrdinalIgnoreCase)
                ? "steel erection"
                : "foundation pour stripping");
        }

        return gaps
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    public static Result FromPlanSilence(ApprenticeLessonPlanComposer.Result plan) =>
        Silenced(plan.SilenceReason ?? "No closed job lesson or textbook grounding. Auricrux will not invent a plan.");

    public static Result Close(
        Request request,
        ApprenticeLessonPlanComposer.Result planResult,
        ApprenticeLessonPlanComposer.JobLessonGrounding? job,
        int textbookHits,
        PriorTurn? prior,
        SystemSnapshot? systemAfter,
        ActSnapshot act)
    {
        if (planResult.Silence || planResult.Plan is null)
            return FromPlanSilence(planResult);

        var plan = planResult.Plan;
        var field = (request.FieldActivity ?? "").Trim();
        var cycleNumber = (prior?.CycleNumber ?? 0) + 1;
        var priorConfirmed = prior is not null
            && (string.Equals(prior.ProposedAction, job?.ProposedAction, StringComparison.OrdinalIgnoreCase)
                || prior.ActApplied);
        var holdStill = systemAfter?.HoldActive == true && (prior?.ActApplied == true || act.Applied);

        var cycle = new Cycle(
            Guid.NewGuid().ToString("n"),
            cycleNumber,
            plan.ApprenticeId,
            plan.Role,
            plan.Slice,
            plan.ProjectId,
            field,
            $"Observe: apprentice is in the field doing '{field}'.",
            job is null
                ? "Understand: no closed job lesson is available for this project."
                : $"Understand: closed job lesson '{job.Topic}' proposes {job.ProposedAction}.",
            $"Learn: composed plan {plan.PlanId} with a field-now module connecting current work to the job lesson.",
            act.Applied
                ? $"Act: applied {act.Action} to {act.MutationTarget}. No PM/finance mutation."
                : $"Act: {act.Reason}",
            cycleNumber == 1
                ? "Improve: first turn recorded. The next observation on this apprentice and project will confirm this lesson."
                : priorConfirmed
                    ? "Improve: prior field lesson is in force. Same job-derived act confirmed. Not unique synthesis."
                    : "Improve: another turn recorded against the same apprentice and project.",
            textbookHits > 0
                ? $"Connect: {textbookHits} textbook hit(s) ground the study modules. Catalog not actuated."
                : "Connect: no textbook hits this turn. Catalog not actuated.",
            plan.PlanId,
            job?.LessonId,
            job?.ProposedAction,
            act.Applied,
            priorConfirmed,
            holdStill,
            UniqueSynthesis: false,
            CatalogActuated: false,
            PmOrFinanceMutated: false,
            DateTime.UtcNow);

        return new Result(
            Silence: false,
            SilenceReason: null,
            Cycle: cycle,
            Plan: plan,
            Act: act,
            UniqueSynthesis: false,
            CatalogMatching: false,
            CatalogActuated: false,
            PmOrFinanceMutated: false);
    }

    private static Result Silenced(string reason) =>
        new(
            true,
            reason,
            null,
            null,
            new ActSnapshot(false, false, false, null, reason, null, null),
            false,
            false,
            false,
            false);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
