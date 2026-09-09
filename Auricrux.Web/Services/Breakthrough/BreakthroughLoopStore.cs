using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using AtlasClient = Auricrux.Web.Services.AtlasService;

namespace Auricrux.Web.Services.Breakthrough;

/// <summary>
/// Process-lifetime memory for the NSF self-correction loop.
/// Atlas remains the durable store when configured; this is the source of truth
/// for the current process so GET-by-id, dashboard, and predictive control stay consistent.
/// </summary>
public sealed class BreakthroughLoopStore
{
    private readonly AtlasClient? _atlas;
    private readonly ConcurrentDictionary<string, byte> _fieldLessonIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConstructionHypothesis> _hypotheses = new();
    private readonly ConcurrentDictionary<string, HypothesisComparison> _decisions = new();
    private readonly ConcurrentDictionary<string, ProvableReasoningResult> _proofs = new();
    private readonly ConcurrentBag<PhysicalVerificationResult> _verifications = [];
    private readonly ConcurrentBag<ControlRecommendation> _recommendations = [];
    private readonly ConcurrentBag<FieldLessonRecord> _fieldLessons = [];
    private readonly ConcurrentBag<PedagogyActRecord> _pedagogyActs = [];
    private readonly ConcurrentDictionary<string, PourControlRecord> _pourControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ErectionControlRecord> _erectionControls = new(StringComparer.OrdinalIgnoreCase);

    public const int HoldStripExtraDays = 3;
    public const int HoldErectionExtraDays = 2;
    public const string PourControlMutationTarget = "pour-control-process-memory";
    public const string ErectionControlMutationTarget = "erection-control-process-memory";
    public const string DefaultPourProjectId = "demo-foundation-pour";
    public const string DefaultSteelProjectId = "demo-structural-steel";
    public const string ProcessMemoryDurableStore = "process-memory";
    public const string AtlasDurableStore = "atlas";
    public const string FieldLessonsCollection = "field_lessons";
    public const string PourControlsCollection = "pour_controls";
    public const string ErectionControlsCollection = "erection_controls";

    public BreakthroughLoopStore(AtlasClient? atlas = null) => _atlas = atlas;

    public bool AtlasConfigured => _atlas?.IsConfigured == true;

    public void CacheComparison(HypothesisComparison comparison)
    {
        _decisions[comparison.DecisionId] = comparison;
        foreach (var h in comparison.Hypotheses)
        {
            _hypotheses[h.HypothesisId] = h;
        }
    }

    public void CacheHypothesis(ConstructionHypothesis hypothesis) =>
        _hypotheses[hypothesis.HypothesisId] = hypothesis;

    public ConstructionHypothesis? FindHypothesis(string hypothesisId) =>
        _hypotheses.TryGetValue(hypothesisId, out var h) ? h : null;

    public HypothesisComparison? FindDecision(string decisionId) =>
        _decisions.TryGetValue(decisionId, out var d) ? d : null;

    public void CacheProof(string proofId, ProvableReasoningResult proof) =>
        _proofs[proofId] = proof;

    public ProvableReasoningResult? FindProof(string proofId) =>
        _proofs.TryGetValue(proofId, out var p) ? p : null;

    public void AddVerification(PhysicalVerificationResult verification) =>
        _verifications.Add(verification);

    public IReadOnlyList<PhysicalVerificationResult> ListVerifications(DateTime cutoffUtc) =>
        _verifications.Where(v => v.VerifiedAt >= cutoffUtc).OrderByDescending(v => v.VerifiedAt).ToList();

    public int DecisionCount => _decisions.Count;

    public int ProofCount => _proofs.Count;

    public void AddControlRecommendation(ControlRecommendation recommendation) =>
        _recommendations.Add(recommendation);

    public IReadOnlyList<ControlRecommendation> ListControlRecommendations(string projectId) =>
        _recommendations
            .Where(r => string.Equals(r.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToList();

    public void AddFieldLesson(FieldLessonRecord lesson)
    {
        if (!_fieldLessonIds.TryAdd(lesson.LessonId, 0))
            return;
        _fieldLessons.Add(lesson);
    }

    public IReadOnlyList<FieldLessonRecord> ListFieldLessons(string? projectId, int limit = 20)
    {
        var rows = _fieldLessons.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            rows = rows.Where(l => string.Equals(l.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        }

        return rows
            .OrderByDescending(l => l.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    public void AddPedagogyAct(PedagogyActRecord act) =>
        _pedagogyActs.Add(act);

    public IReadOnlyList<PedagogyActRecord> ListPedagogyActs(string? projectId, int limit = 20)
    {
        var rows = _pedagogyActs.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            rows = rows.Where(a => string.Equals(a.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        }

        return rows
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList();
    }

    public PourControlRecord? GetPourControl(string? projectId)
    {
        var key = NormalizeProjectId(projectId);
        return _pourControls.TryGetValue(key, out var row) ? row : null;
    }

    public PourControlRecord EnsurePourControl(string? projectId, int baselineStripDays)
    {
        var key = NormalizeProjectId(projectId);
        var baseline = Math.Clamp(baselineStripDays, 1, 90);
        return _pourControls.AddOrUpdate(
            key,
            _ => PourControlRecord.Create(key, baseline, hold: false, actId: null),
            (_, existing) => existing);
    }

    public PourControlRecord HoldStrip(string? projectId, string actId)
    {
        var key = NormalizeProjectId(projectId);
        return _pourControls.AddOrUpdate(
            key,
            _ => PourControlRecord.Create(key, 7, hold: true, actId),
            (_, existing) => existing.WithHold(actId));
    }

    public PourControlRecord ProceedStrip(string? projectId, string actId)
    {
        var key = NormalizeProjectId(projectId);
        return _pourControls.AddOrUpdate(
            key,
            _ => PourControlRecord.Create(key, 7, hold: false, actId),
            (_, existing) => existing.WithProceed(actId));
    }

    public ErectionControlRecord? GetErectionControl(string? projectId)
    {
        var key = NormalizeSteelProjectId(projectId);
        return _erectionControls.TryGetValue(key, out var row) ? row : null;
    }

    public ErectionControlRecord EnsureErectionControl(string? projectId)
    {
        var key = NormalizeSteelProjectId(projectId);
        return _erectionControls.AddOrUpdate(
            key,
            _ => ErectionControlRecord.Create(key, hold: false, actId: null),
            (_, existing) => existing);
    }

    public ErectionControlRecord HoldErection(string? projectId, string actId)
    {
        var key = NormalizeSteelProjectId(projectId);
        return _erectionControls.AddOrUpdate(
            key,
            _ => ErectionControlRecord.Create(key, hold: true, actId),
            (_, existing) => existing.WithHold(actId));
    }

    public ErectionControlRecord ProceedErection(string? projectId, string actId)
    {
        var key = NormalizeSteelProjectId(projectId);
        return _erectionControls.AddOrUpdate(
            key,
            _ => ErectionControlRecord.Create(key, hold: false, actId),
            (_, existing) => existing.WithProceed(actId));
    }

    public async Task<bool> TryPersistFieldLessonAsync(FieldLessonRecord lesson, CancellationToken ct = default)
    {
        if (!AtlasConfigured)
            return false;
        try
        {
            var docs = _atlas!.Database!.GetCollection<BsonDocument>(FieldLessonsCollection);
            var doc = new BsonDocument
            {
                ["_id"] = lesson.LessonId,
                ["project_id"] = lesson.ProjectId,
                ["slice"] = lesson.Slice,
                ["proposed_action"] = lesson.ProposedAction,
                ["topic"] = lesson.Topic,
                ["lesson"] = lesson.Lesson,
                ["source_decision_id"] = lesson.SourceDecisionId,
                ["durable_store"] = AtlasDurableStore,
                ["catalog_match_is_not_synthesis"] = lesson.CatalogMatchIsNotSynthesis,
                ["created_at_utc"] = lesson.CreatedAtUtc
            };
            await docs.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", lesson.LessonId),
                doc,
                new ReplaceOptions { IsUpsert = true },
                ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryPersistPourControlAsync(PourControlRecord control, CancellationToken ct = default)
    {
        if (!AtlasConfigured)
            return false;
        try
        {
            var docs = _atlas!.Database!.GetCollection<BsonDocument>(PourControlsCollection);
            var doc = new BsonDocument
            {
                ["_id"] = control.ProjectId,
                ["project_id"] = control.ProjectId,
                ["baseline_strip_days"] = control.BaselineStripDays,
                ["current_strip_days"] = control.CurrentStripDays,
                ["hold_active"] = control.HoldActive,
                ["pour_at_utc"] = control.PourAtUtc,
                ["planned_strip_at_utc"] = control.PlannedStripAtUtc,
                ["current_strip_at_utc"] = control.CurrentStripAtUtc,
                ["mutation_target"] = control.MutationTarget,
                ["pm_or_finance_mutated"] = control.PmOrFinanceMutated,
                ["last_act_id"] = control.LastActId ?? "",
                ["updated_at_utc"] = control.UpdatedAtUtc
            };
            await docs.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", control.ProjectId),
                doc,
                new ReplaceOptions { IsUpsert = true },
                ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TryPersistErectionControlAsync(ErectionControlRecord control, CancellationToken ct = default)
    {
        if (!AtlasConfigured)
            return false;
        try
        {
            var docs = _atlas!.Database!.GetCollection<BsonDocument>(ErectionControlsCollection);
            var doc = new BsonDocument
            {
                ["_id"] = control.ProjectId,
                ["project_id"] = control.ProjectId,
                ["baseline_erection_days"] = control.BaselineErectionDays,
                ["current_erection_days"] = control.CurrentErectionDays,
                ["hold_active"] = control.HoldActive,
                ["planned_at_utc"] = control.PlannedAtUtc,
                ["planned_erection_at_utc"] = control.PlannedErectionAtUtc,
                ["current_erection_at_utc"] = control.CurrentErectionAtUtc,
                ["mutation_target"] = control.MutationTarget,
                ["pm_or_finance_mutated"] = control.PmOrFinanceMutated,
                ["last_act_id"] = control.LastActId ?? "",
                ["updated_at_utc"] = control.UpdatedAtUtc
            };
            await docs.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", control.ProjectId),
                doc,
                new ReplaceOptions { IsUpsert = true },
                ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task HydrateFromAtlasAsync(CancellationToken ct = default)
    {
        if (!AtlasConfigured)
            return;
        try
        {
            var lessons = await _atlas!.Database!.GetCollection<BsonDocument>(FieldLessonsCollection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Descending("created_at_utc"))
                .Limit(200)
                .ToListAsync(ct);
            foreach (var doc in lessons)
            {
                AddFieldLesson(new FieldLessonRecord
                {
                    LessonId = doc.GetValue("_id", "").ToString() ?? "",
                    ProjectId = doc.GetValue("project_id", "").AsString,
                    Slice = doc.GetValue("slice", "").AsString,
                    ProposedAction = doc.GetValue("proposed_action", "").AsString,
                    Topic = doc.GetValue("topic", "").AsString,
                    Lesson = doc.GetValue("lesson", "").AsString,
                    SourceDecisionId = doc.GetValue("source_decision_id", "").AsString,
                    DurableStore = AtlasDurableStore,
                    CatalogMatchIsNotSynthesis = doc.GetValue("catalog_match_is_not_synthesis", true).ToBoolean(),
                    CreatedAtUtc = ReadUtc(doc, "created_at_utc")
                });
            }

            var pours = await _atlas.Database.GetCollection<BsonDocument>(PourControlsCollection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(ct);
            foreach (var doc in pours)
            {
                var projectId = doc.GetValue("project_id", "").AsString;
                if (string.IsNullOrWhiteSpace(projectId))
                    continue;
                var pourAt = ReadUtc(doc, "pour_at_utc");
                var baseline = doc.GetValue("baseline_strip_days", 7).ToInt32();
                var current = doc.GetValue("current_strip_days", baseline).ToInt32();
                _pourControls[projectId] = new PourControlRecord
                {
                    ProjectId = projectId,
                    BaselineStripDays = baseline,
                    CurrentStripDays = current,
                    HoldActive = doc.GetValue("hold_active", false).ToBoolean(),
                    PourAtUtc = pourAt,
                    PlannedStripAtUtc = ReadUtc(doc, "planned_strip_at_utc", pourAt.AddDays(baseline)),
                    CurrentStripAtUtc = ReadUtc(doc, "current_strip_at_utc", pourAt.AddDays(current)),
                    MutationTarget = doc.GetValue("mutation_target", PourControlMutationTarget).AsString,
                    PmOrFinanceMutated = false,
                    LastActId = doc.GetValue("last_act_id", "").AsString,
                    UpdatedAtUtc = ReadUtc(doc, "updated_at_utc")
                };
            }

            var erections = await _atlas.Database.GetCollection<BsonDocument>(ErectionControlsCollection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(ct);
            foreach (var doc in erections)
            {
                var projectId = doc.GetValue("project_id", "").AsString;
                if (string.IsNullOrWhiteSpace(projectId))
                    continue;
                var plannedAt = ReadUtc(doc, "planned_at_utc");
                var baseline = doc.GetValue("baseline_erection_days", 0).ToInt32();
                var current = doc.GetValue("current_erection_days", baseline).ToInt32();
                _erectionControls[projectId] = new ErectionControlRecord
                {
                    ProjectId = projectId,
                    BaselineErectionDays = baseline,
                    CurrentErectionDays = current,
                    HoldActive = doc.GetValue("hold_active", false).ToBoolean(),
                    PlannedAtUtc = plannedAt,
                    PlannedErectionAtUtc = ReadUtc(doc, "planned_erection_at_utc", plannedAt.AddDays(baseline)),
                    CurrentErectionAtUtc = ReadUtc(doc, "current_erection_at_utc", plannedAt.AddDays(current)),
                    MutationTarget = doc.GetValue("mutation_target", ErectionControlMutationTarget).AsString,
                    PmOrFinanceMutated = false,
                    LastActId = doc.GetValue("last_act_id", "").AsString,
                    UpdatedAtUtc = ReadUtc(doc, "updated_at_utc")
                };
            }
        }
        catch
        {
            // Process memory remains the in-process source of truth.
        }
    }

    public async Task<NsfAtlasDurabilityStatus> GetDurabilityStatusAsync(CancellationToken ct = default)
    {
        if (!AtlasConfigured)
            return new NsfAtlasDurabilityStatus(false, "not_configured", 0, 0, 0, 0, 0);
        try
        {
            var db = _atlas!.Database!;
            var comparisons = await db.GetCollection<BsonDocument>("hypothesis_comparisons").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            var lessons = await db.GetCollection<BsonDocument>(FieldLessonsCollection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            var pours = await db.GetCollection<BsonDocument>(PourControlsCollection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            var erections = await db.GetCollection<BsonDocument>(ErectionControlsCollection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
            var textbooks = await db.GetCollection<BsonDocument>("chunks").CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("domain", "academy-textbook"),
                cancellationToken: ct);
            return new NsfAtlasDurabilityStatus(true, "ok", comparisons, lessons, pours, erections, textbooks);
        }
        catch
        {
            return new NsfAtlasDurabilityStatus(true, "unreachable", 0, 0, 0, 0, 0);
        }
    }

    private static DateTime ReadUtc(BsonDocument doc, string field, DateTime? fallback = null)
    {
        if (!doc.Contains(field) || doc[field].IsBsonNull)
            return fallback ?? DateTime.UtcNow;
        var value = doc[field];
        if (value.IsValidDateTime)
            return DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
        if (value.IsString && DateTime.TryParse(value.AsString, out var parsed))
            return DateTime.SpecifyKind(parsed.ToUniversalTime(), DateTimeKind.Utc);
        return fallback ?? DateTime.UtcNow;
    }

    private static string NormalizeProjectId(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? DefaultPourProjectId : projectId.Trim();

    private static string NormalizeSteelProjectId(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? DefaultSteelProjectId : projectId.Trim();
}

public sealed record NsfAtlasDurabilityStatus(
    bool Configured,
    string Status,
    long HypothesisComparisons,
    long FieldLessons,
    long PourControls,
    long ErectionControls,
    long TextbookChunks = 0);

/// <summary>
/// Actionable control item derived from a closed breakthrough loop — not a job-cost mutation.
/// </summary>
public sealed class ControlRecommendation
{
    public required string RecommendationId { get; init; }
    public required string ProjectId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Timeframe { get; init; }
    public required string SourceDecisionId { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Job-derived field lesson from a closed loop. Process memory only unless Atlas is later configured.
/// Catalog matching is not lesson synthesis.
/// </summary>
public sealed class FieldLessonRecord
{
    public required string LessonId { get; init; }
    public required string ProjectId { get; init; }
    public required string Slice { get; init; }
    public required string ProposedAction { get; init; }
    public required string Topic { get; init; }
    public required string Lesson { get; init; }
    public required string SourceDecisionId { get; init; }
    public string DurableStore { get; init; } = "process-memory";
    public bool CatalogMatchIsNotSynthesis { get; init; } = true;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed class PedagogyActRecord
{
    public required string ActId { get; init; }
    public required string ProjectId { get; init; }
    public required string Slice { get; init; }
    public required string Action { get; init; }
    public required string DecisionId { get; init; }
    public required string VerificationId { get; init; }
    public required bool Accepted { get; init; }
    public required bool MutationApplied { get; init; }
    public required string Reason { get; init; }
    public bool CatalogMatchIsNotSynthesis { get; init; } = true;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Auricrux-owned pour stripping control. Not a PM schedule row and not a finance table.
/// A proof-gated hold-strip moves CurrentStripAtUtc; Atlas is not required.
/// </summary>
public sealed record PourControlRecord
{
    public required string ProjectId { get; init; }
    public required int BaselineStripDays { get; init; }
    public required int CurrentStripDays { get; init; }
    public required bool HoldActive { get; init; }
    public required DateTime PourAtUtc { get; init; }
    public required DateTime PlannedStripAtUtc { get; init; }
    public required DateTime CurrentStripAtUtc { get; init; }
    public string MutationTarget { get; init; } = BreakthroughLoopStore.PourControlMutationTarget;
    public bool PmOrFinanceMutated { get; init; }
    public string? LastActId { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public static PourControlRecord Create(string projectId, int baselineDays, bool hold, string? actId)
    {
        var pourAt = DateTime.UtcNow;
        var current = hold ? baselineDays + BreakthroughLoopStore.HoldStripExtraDays : baselineDays;
        return new PourControlRecord
        {
            ProjectId = projectId,
            BaselineStripDays = baselineDays,
            CurrentStripDays = current,
            HoldActive = hold,
            PourAtUtc = pourAt,
            PlannedStripAtUtc = pourAt.AddDays(baselineDays),
            CurrentStripAtUtc = pourAt.AddDays(current),
            LastActId = actId,
            PmOrFinanceMutated = false,
            UpdatedAtUtc = pourAt
        };
    }

    public PourControlRecord WithHold(string actId)
    {
        var current = BaselineStripDays + BreakthroughLoopStore.HoldStripExtraDays;
        return this with
        {
            CurrentStripDays = current,
            HoldActive = true,
            CurrentStripAtUtc = PourAtUtc.AddDays(current),
            LastActId = actId,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public PourControlRecord WithProceed(string actId) =>
        this with
        {
            CurrentStripDays = BaselineStripDays,
            HoldActive = false,
            CurrentStripAtUtc = PourAtUtc.AddDays(BaselineStripDays),
            LastActId = actId,
            UpdatedAtUtc = DateTime.UtcNow
        };
}

/// <summary>
/// Auricrux-owned steel erection control. Not a PM schedule row and not a finance table.
/// A proof-gated hold-erection moves CurrentErectionAtUtc; Atlas is not required.
/// </summary>
public sealed record ErectionControlRecord
{
    public required string ProjectId { get; init; }
    public required int BaselineErectionDays { get; init; }
    public required int CurrentErectionDays { get; init; }
    public required bool HoldActive { get; init; }
    public required DateTime PlannedAtUtc { get; init; }
    public required DateTime PlannedErectionAtUtc { get; init; }
    public required DateTime CurrentErectionAtUtc { get; init; }
    public string MutationTarget { get; init; } = BreakthroughLoopStore.ErectionControlMutationTarget;
    public bool PmOrFinanceMutated { get; init; }
    public string? LastActId { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public static ErectionControlRecord Create(string projectId, bool hold, string? actId)
    {
        var plannedAt = DateTime.UtcNow;
        const int baseline = 0;
        var current = hold ? baseline + BreakthroughLoopStore.HoldErectionExtraDays : baseline;
        return new ErectionControlRecord
        {
            ProjectId = projectId,
            BaselineErectionDays = baseline,
            CurrentErectionDays = current,
            HoldActive = hold,
            PlannedAtUtc = plannedAt,
            PlannedErectionAtUtc = plannedAt.AddDays(baseline),
            CurrentErectionAtUtc = plannedAt.AddDays(current),
            LastActId = actId,
            PmOrFinanceMutated = false,
            UpdatedAtUtc = plannedAt
        };
    }

    public ErectionControlRecord WithHold(string actId)
    {
        var current = BaselineErectionDays + BreakthroughLoopStore.HoldErectionExtraDays;
        return this with
        {
            CurrentErectionDays = current,
            HoldActive = true,
            CurrentErectionAtUtc = PlannedAtUtc.AddDays(current),
            LastActId = actId,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public ErectionControlRecord WithProceed(string actId) =>
        this with
        {
            CurrentErectionDays = BaselineErectionDays,
            HoldActive = false,
            CurrentErectionAtUtc = PlannedAtUtc.AddDays(BaselineErectionDays),
            LastActId = actId,
            UpdatedAtUtc = DateTime.UtcNow
        };
}
