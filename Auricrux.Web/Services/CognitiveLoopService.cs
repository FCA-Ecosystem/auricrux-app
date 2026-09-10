using Auricrux.Web.Services.Breakthrough;
using Auricrux.Web.Services.PhaseII;

namespace Auricrux.Web.Services;

/// <summary>
/// Closed field↔lesson↔system turn. Observes what the apprentice is doing now,
/// composes a job-grounded lesson, applies that lesson to Auricrux controls when
/// human-accepted and proof-gated, and confirms the prior turn on the next observation.
/// Does not invent unique synthesis or actuate Academy catalog / PM / finance.
/// </summary>
public sealed class CognitiveLoopService
{
    private readonly BreakthroughLoopStore _loop;
    private readonly ApprenticeLessonPlanService _plans;
    private readonly PedagogyActService _acts;
    private readonly TextbookCorpusService _textbooks;

    public CognitiveLoopService(
        BreakthroughLoopStore loop,
        ApprenticeLessonPlanService plans,
        PedagogyActService acts,
        TextbookCorpusService textbooks)
    {
        _loop = loop;
        _plans = plans;
        _acts = acts;
        _textbooks = textbooks;
    }

    public async Task<CognitiveLoopComposer.Result> RunAsync(
        CognitiveLoopComposer.Request request,
        CancellationToken ct = default)
    {
        var incomplete = CognitiveLoopComposer.Incomplete(request);
        if (incomplete is not null)
            return incomplete;

        var slice = string.IsNullOrWhiteSpace(request.Slice) ? "foundation-pour" : request.Slice.Trim();
        var gaps = CognitiveLoopComposer.InferGaps(request.FieldActivity, slice, request.KnownGaps);
        var planRequest = new ApprenticeLessonPlanComposer.Request(
            request.ApprenticeId,
            request.Role,
            slice,
            request.ProjectId,
            gaps,
            request.FieldActivity);
        var planResult = await _plans.ComposeAsync(planRequest, ct);
        if (planResult.Silence)
            return CognitiveLoopComposer.FromPlanSilence(planResult);

        var job = _plans.LatestJobLesson(request.ProjectId, slice);
        var priorCycle = _loop.ListCognitiveCycles(request.ApprenticeId, request.ProjectId, 1).FirstOrDefault();
        var prior = priorCycle is null
            ? null
            : new CognitiveLoopComposer.PriorTurn(
                priorCycle.CycleId,
                priorCycle.FieldActivity,
                priorCycle.ProposedAction,
                priorCycle.ActApplied,
                priorCycle.CycleNumber);

        var act = await ApplyLessonAsync(request, job, slice);
        var system = Snapshot(request.ProjectId, slice);
        var textbookHits = 0;
        if (_textbooks.IsAtlasActive)
        {
            var query = ApprenticeLessonPlanComposer.TextbookQuery(planRequest);
            if (!string.IsNullOrWhiteSpace(query))
            {
                var hits = await _textbooks.SearchAsync(query, 4, ct);
                textbookHits = hits.Count;
            }
        }

        var result = CognitiveLoopComposer.Close(
            request,
            planResult,
            job,
            textbookHits,
            prior,
            system,
            act);
        if (result.Cycle is not null)
            await _loop.RememberCognitiveCycleAsync(result.Cycle, ct);
        return result;
    }

    public IReadOnlyList<CognitiveLoopComposer.Cycle> List(string? apprenticeId, string? projectId, int limit = 20) =>
        _loop.ListCognitiveCycles(apprenticeId, projectId, limit);

    private async Task<CognitiveLoopComposer.ActSnapshot> ApplyLessonAsync(
        CognitiveLoopComposer.Request request,
        ApprenticeLessonPlanComposer.JobLessonGrounding? job,
        string slice)
    {
        var proposed = (job?.ProposedAction ?? "").Trim();
        if (!request.HumanAccepted)
        {
            return new CognitiveLoopComposer.ActSnapshot(
                true,
                false,
                false,
                proposed,
                "Human accept required before the lesson applies to Auricrux controls.",
                null,
                null);
        }

        if (string.IsNullOrWhiteSpace(proposed) || !PedagogyActService.AllowedActions.Contains(proposed))
        {
            return new CognitiveLoopComposer.ActSnapshot(
                false,
                false,
                false,
                proposed,
                "No Auricrux control action on this job lesson. Plan composed.",
                null,
                null);
        }

        if (string.IsNullOrWhiteSpace(request.DecisionId) || string.IsNullOrWhiteSpace(request.VerificationId))
        {
            return new CognitiveLoopComposer.ActSnapshot(
                false,
                false,
                false,
                proposed,
                "Lesson composed. Proof ids required before applying to Auricrux controls.",
                null,
                null);
        }

        var executed = await _acts.Execute(new PedagogyActRequest
        {
            Action = proposed,
            Slice = slice,
            ProjectId = request.ProjectId,
            DecisionId = request.DecisionId,
            VerificationId = request.VerificationId,
            HumanAccepted = true
        });
        return new CognitiveLoopComposer.ActSnapshot(
            true,
            executed.Accepted,
            executed.MutationApplied,
            executed.Action,
            executed.Reason,
            executed.MutationTarget,
            executed.HoldActive);
    }

    private CognitiveLoopComposer.SystemSnapshot Snapshot(string? projectId, string slice)
    {
        var steel = slice.Contains("steel", StringComparison.OrdinalIgnoreCase);
        if (steel)
        {
            var erection = _loop.GetErectionControl(projectId);
            return new CognitiveLoopComposer.SystemSnapshot(
                erection?.HoldActive == true,
                erection?.HoldActive == true ? BreakthroughLoopStore.ErectionControlMutationTarget : null,
                erection?.CurrentErectionDays,
                erection?.BaselineErectionDays);
        }

        var pour = _loop.GetPourControl(projectId);
        return new CognitiveLoopComposer.SystemSnapshot(
            pour?.HoldActive == true,
            pour?.HoldActive == true ? BreakthroughLoopStore.PourControlMutationTarget : null,
            pour?.CurrentStripDays,
            pour?.BaselineStripDays);
    }
}
