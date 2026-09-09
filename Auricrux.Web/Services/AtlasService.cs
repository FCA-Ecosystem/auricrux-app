using MongoDB.Bson;
using MongoDB.Driver;

namespace Auricrux.Web.Services;

/// <summary>
/// Unified MongoDB Atlas client for Auricrux App.
///
/// Provides:
///  - Corpus collection (construction knowledge, vector/text search)
///  - Conversation memory collection (persistent chat history)
///  - Model routing rules collection (staged intelligence tiers)
///  - Feedback collection
///
/// Activated when Atlas:ConnectionString is set. All callers must call
/// IsConfigured before using — if not configured, services fall back to
/// local implementations (SQLite / in-memory corpus).
///
/// Atlas indexes to create in Atlas UI:
///   corpus:   text search index "corpus_text_search" on fields: content, title, tags
///   corpus:   vector search index "corpus_vector" on field: embedding (dims per model)
///   memory:   TTL index on "created_at" for optional expiry (set in Atlas UI)
/// </summary>
public sealed class AtlasService : IDisposable
{
    private readonly MongoClient? _client;
    private readonly IMongoDatabase? _db;
    private readonly ILogger<AtlasService> _logger;

    public bool IsConfigured => _client is not null;

    /// <summary>Raw database handle for breakthrough collections (hypotheses, verifications, proofs).</summary>
    public IMongoDatabase? Database => _db;

    public AtlasService(IConfiguration configuration, ILogger<AtlasService> logger)
    {
        _logger = logger;
        var conn = configuration["Atlas:ConnectionString"];
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogInformation("Atlas:ConnectionString not set — Atlas features disabled, using local fallbacks.");
            return;
        }

        try
        {
            _client = new MongoClient(conn);
            _db = _client.GetDatabase(configuration["Atlas:Database"] ?? "auricrux");
            _logger.LogInformation("Atlas client initialised (database={Db})", _db.DatabaseNamespace.DatabaseName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Atlas initialisation failed — falling back to local storage");
        }
    }

    // ── Collections ───────────────────────────────────────────────────────────

    public IMongoCollection<BsonDocument> Corpus =>
        _db!.GetCollection<BsonDocument>("corpus");

    /// <summary>RAG knowledge chunks, including Academy textbook ingest (`domain=academy-textbook`).</summary>
    public IMongoCollection<BsonDocument> Chunks =>
        _db!.GetCollection<BsonDocument>("chunks");

    public IMongoCollection<BsonDocument> Memory =>
        _db!.GetCollection<BsonDocument>("conversation_memory");

    public IMongoCollection<BsonDocument> ModelRoutes =>
        _db!.GetCollection<BsonDocument>("model_routes");

    public IMongoCollection<BsonDocument> Feedback =>
        _db!.GetCollection<BsonDocument>("feedback");

    public IMongoCollection<BsonDocument> Interactions =>
        _db!.GetCollection<BsonDocument>("interactions");

    public IMongoCollection<BsonDocument> ConstructionEvents =>
        _db!.GetCollection<BsonDocument>("construction_events");

    public IMongoCollection<BsonDocument> ConstructionOutcomes =>
        _db!.GetCollection<BsonDocument>("construction_outcomes");

    public IMongoCollection<BsonDocument> ConstructionEvidence =>
        _db!.GetCollection<BsonDocument>("construction_evidence");

    public IMongoCollection<BsonDocument> GuidanceEffectiveness =>
        _db!.GetCollection<BsonDocument>("guidance_effectiveness");

    public IMongoCollection<BsonDocument> LearningRecommendations =>
        _db!.GetCollection<BsonDocument>("learning_recommendations");

    public IMongoCollection<BsonDocument> QualityMetrics =>
        _db!.GetCollection<BsonDocument>("quality_metrics");

    public IMongoCollection<BsonDocument> AuditTrail =>
        _db!.GetCollection<BsonDocument>("audit_trail");

    public IMongoCollection<BsonDocument> KnowledgeGaps =>
        _db!.GetCollection<BsonDocument>("knowledge_gaps");

    public IMongoCollection<BsonDocument> ImprovementProposals =>
        _db!.GetCollection<BsonDocument>("improvement_proposals");

    // ── Health ────────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string Status)> PingAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return (false, "not_configured");
        try
        {
            await _db!.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return (true, "ok");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Index management ──────────────────────────────────────────────────────

    /// <summary>
    /// Ensure indexes exist for interactions and feedback collections.
    /// Safe to call multiple times - existing indexes are ignored.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return;

        try
        {
            // Interactions: compound index on interaction_id (unique) + created_at for time-based queries
            var interactionIdIndex = Builders<BsonDocument>.IndexKeys.Ascending("interaction_id");
            await Interactions.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(interactionIdIndex,
                    new CreateIndexOptions { Background = true, Unique = true }),
                cancellationToken: ct);

            var interactionDateIndex = Builders<BsonDocument>.IndexKeys.Ascending("created_at");
            await Interactions.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(interactionDateIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            // Feedback: compound index on interaction_id + stars + created_at for gap analysis queries
            var feedbackInteractionIndex = Builders<BsonDocument>.IndexKeys
                .Ascending("interaction_id")
                .Ascending("stars")
                .Ascending("created_at");
            await Feedback.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(feedbackInteractionIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            // Construction Events: indexes for event queries by project, user, type, date
            var eventProjectIndex = Builders<BsonDocument>.IndexKeys
                .Ascending("project_id")
                .Ascending("timestamp");
            await ConstructionEvents.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(eventProjectIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            var eventUserIndex = Builders<BsonDocument>.IndexKeys
                .Ascending("user_id")
                .Ascending("timestamp");
            await ConstructionEvents.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(eventUserIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            var eventInteractionIndex = Builders<BsonDocument>.IndexKeys.Ascending("interaction_id");
            await ConstructionEvents.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(eventInteractionIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            // Construction Outcomes: index on event_id for outcome lookups
            var outcomeEventIndex = Builders<BsonDocument>.IndexKeys.Ascending("event_id");
            await ConstructionOutcomes.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(outcomeEventIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            // Construction Evidence: index on outcome_id for evidence lookups
            var evidenceOutcomeIndex = Builders<BsonDocument>.IndexKeys.Ascending("outcome_id");
            await ConstructionEvidence.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(evidenceOutcomeIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            // Guidance Effectiveness: index on interaction_id and linked_at for effectiveness tracking
            var effectivenessInteractionIndex = Builders<BsonDocument>.IndexKeys.Ascending("interaction_id");
            await GuidanceEffectiveness.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(effectivenessInteractionIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            var effectivenessDateIndex = Builders<BsonDocument>.IndexKeys.Ascending("linked_at");
            await GuidanceEffectiveness.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(effectivenessDateIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            // Learning Recommendations: index on user_id, engagement_status, and generated_at
            var recommendationUserIndex = Builders<BsonDocument>.IndexKeys
                .Ascending("user_id")
                .Ascending("engagement_status");
            await LearningRecommendations.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(recommendationUserIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            var recommendationGapIndex = Builders<BsonDocument>.IndexKeys.Ascending("source_gap_pattern");
            await LearningRecommendations.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(recommendationGapIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            var recommendationDateIndex = Builders<BsonDocument>.IndexKeys.Ascending("generated_at");
            await LearningRecommendations.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(recommendationDateIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            // Quality Metrics: index on period_end for time-series queries
            var qualityMetricsDateIndex = Builders<BsonDocument>.IndexKeys.Descending("period_end");
            await QualityMetrics.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(qualityMetricsDateIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            // Audit Trail: indexes on timestamp, actor_id, resource_id, action_type for queries
            var auditTimestampIndex = Builders<BsonDocument>.IndexKeys.Descending("timestamp");
            await AuditTrail.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(auditTimestampIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            var auditActorIndex = Builders<BsonDocument>.IndexKeys.Ascending("actor_id");
            await AuditTrail.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(auditActorIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            var auditResourceIndex = Builders<BsonDocument>.IndexKeys.Ascending("resource_id");
            await AuditTrail.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(auditResourceIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            var auditActionTypeIndex = Builders<BsonDocument>.IndexKeys.Ascending("action_type");
            await AuditTrail.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(auditActionTypeIndex,
                    new CreateIndexOptions { Background = true }),
                cancellationToken: ct);

            _logger.LogInformation("Atlas indexes ensured for all learning pipeline collections including audit trail");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure Atlas indexes — continuing without index verification");
        }
    }

    public void Dispose() => (_client as IDisposable)?.Dispose();
}
