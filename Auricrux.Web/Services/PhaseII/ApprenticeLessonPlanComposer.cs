namespace Auricrux.Web.Services.PhaseII;

/// <summary>
/// Composes an Auricrux-owned apprentice lesson plan from a specific learner
/// identity plus job evidence and optional textbook grounding.
/// Catalog matching is not lesson synthesis. This compose is not unique synthesis.
/// Incomplete apprentice identity or missing grounding silences the plan.
/// Does not write Academy catalog rows or CTE credentials.
/// </summary>
public static class ApprenticeLessonPlanComposer
{
    public const string GovernanceNote =
        "Auricrux-composed apprentice plan (not catalog matching, not unique synthesis). Grounded in job evidence and optional textbook retrieval.";

    public sealed record TextbookGrounding(string Id, string Program, string SectionHeading, string Snippet);

    public sealed record JobLessonGrounding(
        string LessonId,
        string Slice,
        string Topic,
        string Lesson,
        string? ProposedAction);

    public sealed record Request(
        string? ApprenticeId,
        string? Role,
        string? Slice,
        string? ProjectId,
        IReadOnlyList<string>? KnownGaps);

    public sealed record Module(
        int Order,
        string Kind,
        string Title,
        string Prompt,
        int Minutes,
        string? SourceId,
        string SourceKind);

    public sealed record Plan(
        string PlanId,
        string ApprenticeId,
        string Role,
        string Slice,
        string? ProjectId,
        string Objective,
        string Assessment,
        IReadOnlyList<Module> Modules,
        string GovernanceNote,
        bool UniqueSynthesis,
        bool CatalogMatching,
        bool CatalogActuated);

    public sealed record Result(
        bool Silence,
        string? SilenceReason,
        Plan? Plan,
        bool UniqueSynthesis,
        bool CatalogMatching,
        bool CatalogActuated,
        bool PmOrFinanceMutated);

    public static Result Compose(
        Request request,
        JobLessonGrounding? jobLesson,
        IReadOnlyList<TextbookGrounding> textbooks)
    {
        var apprenticeId = (request.ApprenticeId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(apprenticeId))
            return Silenced("Apprentice identity is incomplete. Auricrux will not invent a learner.");

        var role = NormalizeRole(request.Role);
        if (role is null)
            return Silenced("Role is incomplete. Auricrux will not invent a trade path.");

        var slice = string.IsNullOrWhiteSpace(request.Slice)
            ? (jobLesson?.Slice ?? "foundation-pour")
            : request.Slice.Trim();
        textbooks ??= [];

        if (jobLesson is null && textbooks.Count == 0)
            return Silenced("No closed job lesson or textbook grounding. Auricrux will not invent a plan.");

        var gaps = NormalizeGaps(request.KnownGaps, slice);
        var minutes = MinutesFor(role);
        var modules = new List<Module>();
        var order = 1;

        if (jobLesson is not null)
        {
            modules.Add(new Module(
                order++,
                "job-evidence",
                $"Job evidence for {apprenticeId}: {jobLesson.Topic}",
                $"{jobLesson.Lesson} This module is the closed-loop field lesson for this apprentice, not an Academy catalog row.",
                minutes,
                jobLesson.LessonId,
                "field-lesson"));
        }

        foreach (var gap in gaps)
        {
            var hit = textbooks.FirstOrDefault(t =>
                Contains(t.SectionHeading, gap) || Contains(t.Snippet, gap) || Contains(t.Program, gap));
            hit ??= textbooks.FirstOrDefault();
            var prompt = hit is null
                ? $"Study '{gap}' as a {role} against the job evidence above. Textbook retrieval was empty in this process."
                : $"Study '{gap}' as a {role} using textbook '{hit.SectionHeading}' ({hit.Program}). {hit.Snippet}";
            modules.Add(new Module(
                order++,
                "study",
                $"Study for {role}: {gap}",
                prompt,
                minutes,
                hit?.Id,
                hit is null ? "ungrounded-gap" : "academy-textbook"));
        }

        modules.Add(new Module(
            order,
            "demonstrate",
            $"Demonstrate for {role} {apprenticeId}",
            AssessmentFor(role, slice, jobLesson?.ProposedAction),
            minutes,
            jobLesson?.LessonId,
            "apprentice-demonstration"));

        var plan = new Plan(
            Guid.NewGuid().ToString("n"),
            apprenticeId,
            role,
            slice,
            string.IsNullOrWhiteSpace(request.ProjectId) ? null : request.ProjectId.Trim(),
            $"Tailored plan for {apprenticeId} ({role}) on {slice}. {GovernanceNote}",
            AssessmentFor(role, slice, jobLesson?.ProposedAction),
            modules,
            GovernanceNote,
            UniqueSynthesis: false,
            CatalogMatching: false,
            CatalogActuated: false);

        return new Result(
            Silence: false,
            SilenceReason: null,
            Plan: plan,
            UniqueSynthesis: false,
            CatalogMatching: false,
            CatalogActuated: false,
            PmOrFinanceMutated: false);
    }

    public static string TextbookQuery(Request request)
    {
        var role = NormalizeRole(request.Role) ?? "apprentice";
        var slice = (request.Slice ?? "foundation-pour").Trim();
        var gaps = NormalizeGaps(request.KnownGaps, slice);
        return string.Join(" ", new[] { slice, role }.Concat(gaps));
    }

    private static Result Silenced(string reason) =>
        new(true, reason, null, false, false, false, false);

    private static string? NormalizeRole(string? role)
    {
        var value = (role ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Contains("first") || value is "year-1" or "year1" or "y1")
            return "first-year-apprentice";
        if (value.Contains("journeyman"))
            return "journeyman-candidate";
        return "apprentice";
    }

    private static IReadOnlyList<string> NormalizeGaps(IReadOnlyList<string>? knownGaps, string slice)
    {
        var gaps = (knownGaps ?? [])
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        if (gaps.Count > 0)
            return gaps;
        return slice.Contains("steel", StringComparison.OrdinalIgnoreCase)
            ? ["steel erection"]
            : ["foundation pour stripping"];
    }

    private static int MinutesFor(string role) => role switch
    {
        "first-year-apprentice" => 25,
        "journeyman-candidate" => 15,
        _ => 20
    };

    private static string AssessmentFor(string role, string slice, string? proposedAction)
    {
        var act = string.IsNullOrWhiteSpace(proposedAction) ? "the job-derived control" : proposedAction;
        if (role == "first-year-apprentice")
            return $"Show a competent person the Identify → Assess → Control sequence on this {slice} job before working near {act}. Not a catalog exam.";
        if (role == "journeyman-candidate")
            return $"Teach-back: explain why {act} was proposed and what field evidence would release it. Not unique synthesis.";
        return $"Demonstrate {act} on this {slice} job with a supervisor present. Not a catalog credential.";
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
