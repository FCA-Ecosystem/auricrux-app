using Auricrux.Web.Services.Breakthrough;
using Auricrux.Web.Services.PhaseII;

namespace Auricrux.Web.Services;

/// <summary>
/// Orchestrates apprentice lesson-plan compose from field lessons + textbook retrieval.
/// Persist is additive. Does not actuate Academy catalog or PM/finance.
/// </summary>
public sealed class ApprenticeLessonPlanService
{
    private readonly BreakthroughLoopStore _loop;
    private readonly TextbookCorpusService _textbooks;

    public ApprenticeLessonPlanService(BreakthroughLoopStore loop, TextbookCorpusService textbooks)
    {
        _loop = loop;
        _textbooks = textbooks;
    }

    public async Task<ApprenticeLessonPlanComposer.Result> ComposeAsync(
        ApprenticeLessonPlanComposer.Request request,
        CancellationToken ct = default)
    {
        var job = LatestJobLesson(request.ProjectId, request.Slice);
        var query = ApprenticeLessonPlanComposer.TextbookQuery(request);
        IReadOnlyList<ApprenticeLessonPlanComposer.TextbookGrounding> grounding = [];
        if (_textbooks.IsAtlasActive && !string.IsNullOrWhiteSpace(query))
        {
            var hits = await _textbooks.SearchAsync(query, 4, ct);
            grounding = hits.Select(h => new ApprenticeLessonPlanComposer.TextbookGrounding(
                h.Id,
                h.ProgramTitle,
                h.SectionHeading,
                h.Snippet)).ToList();
        }

        var result = ApprenticeLessonPlanComposer.Compose(request, job, grounding);
        if (!result.Silence && result.Plan is not null)
            await _loop.RememberApprenticeLessonPlanAsync(result.Plan, ct);
        return result;
    }

    public IReadOnlyList<ApprenticeLessonPlanComposer.Plan> List(string? apprenticeId, int limit = 20) =>
        _loop.ListApprenticeLessonPlans(apprenticeId, limit);

    public ApprenticeLessonPlanComposer.JobLessonGrounding? LatestJobLesson(string? projectId, string? slice)
    {
        var lessons = _loop.ListFieldLessons(projectId, 8);
        if (!string.IsNullOrWhiteSpace(slice))
        {
            var sliced = lessons.FirstOrDefault(l =>
                string.Equals(l.Slice, slice, StringComparison.OrdinalIgnoreCase));
            if (sliced is not null)
                lessons = [sliced];
        }

        var lesson = lessons.FirstOrDefault();
        if (lesson is null)
            return null;
        return new ApprenticeLessonPlanComposer.JobLessonGrounding(
            lesson.LessonId,
            lesson.Slice,
            lesson.Topic,
            lesson.Lesson,
            lesson.ProposedAction);
    }
}
