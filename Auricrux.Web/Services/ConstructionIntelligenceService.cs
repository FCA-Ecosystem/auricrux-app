using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Auricrux.Shared.Models;
using Auricrux.Web.Services.PhaseII;
using MongoDB.Bson;

namespace Auricrux.Web.Services;

/// <summary>
/// Multi-model construction AI backend.
/// Model selection is delegated to AuricruxModelRouter (staged intelligence).
/// Corpus search uses Atlas when configured, falls back to local JSON corpus.
/// </summary>
public sealed class ConstructionIntelligenceService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ConstructionIntelligenceService> _logger;
    private readonly List<ConstructionKnowledgeEntry> _corpus;
    private readonly AtlasCorpusService _atlasCorpus;
    private readonly TextbookCorpusService _textbookCorpus;
    private readonly AtlasService _atlas;
    private readonly AuricruxModelRouter _router;
    private readonly ContextAwareGuidanceService? _contextService;
    private readonly CognitiveLoopService? _cognitiveLoop;
    private readonly Dictionary<Guid, ChatResponse> _interactions = new();
    private readonly object _gate = new();

    public ConstructionIntelligenceService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<ConstructionIntelligenceService> logger,
        IHostEnvironment env,
        AtlasCorpusService atlasCorpus,
        TextbookCorpusService textbookCorpus,
        AtlasService atlas,
        AuricruxModelRouter router,
        ContextAwareGuidanceService? contextService = null,
        CognitiveLoopService? cognitiveLoop = null)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _atlasCorpus = atlasCorpus;
        _textbookCorpus = textbookCorpus;
        _atlas = atlas;
        _router = router;
        _contextService = contextService;
        _cognitiveLoop = cognitiveLoop;
        _corpus = LoadCorpus(env.ContentRootPath);
    }

    public int CorpusEntryCount => _corpus.Count;

    public CorpusStatsSnapshot GetCorpusStats()
    {
        var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var internalCount = 0;
        var publicCount = 0;

        foreach (var entry in _corpus)
        {
            if (entry.Scope.Equals("public", StringComparison.OrdinalIgnoreCase))
            {
                publicCount++;
            }
            else
            {
                internalCount++;
            }

            var category = InferCategory(entry);
            categories[category] = categories.GetValueOrDefault(category) + 1;

            foreach (var tag in entry.Tags)
            {
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
            }
        }

        return new CorpusStatsSnapshot
        {
            TotalEntries = _corpus.Count,
            InternalEntries = internalCount,
            PublicEntries = publicCount,
            Categories = categories.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
            TopTags = tagCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }

    public IReadOnlyList<string> AvailableModels => _router.Tiers.All;

    public async Task<ChatResponse> ChatAsync(ChatRequest request, string? model, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Phase 6: Context-aware guidance enhancement
        var effectiveQuery = request.Query;
        string? contextSummary = null;
        if (_contextService != null && 
            (!string.IsNullOrWhiteSpace(request.UserId) || !string.IsNullOrWhiteSpace(request.ProjectId)))
        {
            try
            {
                var contextEnhanced = await _contextService.BuildContextPromptAsync(
                    request.Query,
                    request.UserId,
                    request.ProjectId,
                    request.Role,
                    request.Phase,
                    ct);

                effectiveQuery = contextEnhanced.EnhancedQuery;
                contextSummary = contextEnhanced.ContextSummary;

                _logger.LogInformation("Context-enhanced query: {EventCount} recent events",
                    contextEnhanced.RecentEventCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Context enhancement failed - using original query");
            }
        }

        // Staged intelligence: route to appropriate model tier
        var selection = await _router.SelectAsync(
            request.Query, // Use original query for routing decision
            request.ThinkingMode,
            hasImageAttachment: false,
            clientRequestedModel: model,
            ct);
        var resolvedModel = selection.Model;
        _logger.LogDebug("Model selected: {Model} tier={Tier} reason={Reason}", resolvedModel, selection.Tier, selection.Reason);

        // Corpus search: try Atlas first, fall back to local
        List<Source> sources;
        if (_atlasCorpus.IsAtlasActive)
        {
            sources = await _atlasCorpus.SearchAsync(request.Query, request.SearchScope, take: 5, ct);
            if (sources.Count == 0)
                sources = SearchInternal(request.Query, request.SearchScope, take: 5);
        }
        else
        {
            sources = SearchInternal(request.Query, request.SearchScope, take: 5);
        }

        if (_textbookCorpus.IsAtlasActive)
        {
            var textbookHits = await _textbookCorpus.SearchAsSourcesAsync(request.Query, take: 3, ct);
            if (textbookHits.Count > 0)
                sources = sources.Concat(textbookHits).ToList();
        }

        var thinking = await ThinkAsync(new ThinkingRequest { Query = request.Query, Mode = request.ThinkingMode }, resolvedModel, ct);
        var loopEnvelope = await TryRunCognitiveLoopAsync(request, ct);
        var system = BuildSystemPrompt(request.ThinkingMode, sources, loopEnvelope);
        // Use context-enhanced query for LLM completion
        var content = await CompleteAsync(system, effectiveQuery, request.ConversationHistory, resolvedModel, ct);
        sw.Stop();

        var response = new ChatResponse
        {
            Content = content,
            ThinkingContent = thinking.Result,
            Sources = sources,
            Timestamp = DateTime.UtcNow,
            ProcessingTimeMs = sw.ElapsedMilliseconds,
            ConfidenceScore = sources.Count > 0 ? 0.86 : 0.72,
            InteractionId = Guid.NewGuid(),
            CognitiveLoop = loopEnvelope
        };

        // Persist interaction to in-memory cache (for TryGetInteraction fallback)
        lock (_gate)
        {
            _interactions[response.InteractionId!.Value] = response;
        }

        // Persist interaction to Atlas for durable learning pipeline
        if (_atlas.IsConfigured)
        {
            try
            {
                await _atlas.Interactions.InsertOneAsync(new BsonDocument
                {
                    ["interaction_id"] = response.InteractionId!.Value.ToString(),
                    ["query"] = request.Query,
                    ["response_content"] = response.Content,
                    ["thinking_content"] = response.ThinkingContent,
                    ["sources"] = new BsonArray(response.Sources.Select(s => new BsonDocument
                    {
                        ["title"] = s.Title,
                        ["url"] = s.Url ?? "",
                        ["relevance_score"] = s.RelevanceScore
                    })),
                    ["model"] = resolvedModel,
                    ["model_tier"] = selection.Tier.ToString(),
                    ["selection_reason"] = selection.Reason,
                    ["thinking_mode"] = request.ThinkingMode.ToString(),
                    ["search_scope"] = request.SearchScope.ToString(),
                    ["session_id"] = request.SessionId,
                    ["processing_time_ms"] = response.ProcessingTimeMs,
                    ["confidence_score"] = response.ConfidenceScore,
                    ["created_at"] = response.Timestamp,
                    // Phase 6: Store context information
                    ["user_id"] = request.UserId ?? "",
                    ["project_id"] = request.ProjectId ?? "",
                    ["role"] = request.Role ?? "",
                    ["phase"] = request.Phase ?? "",
                    ["context_summary"] = contextSummary ?? "",
                    ["context_enhanced"] = contextSummary != null,
                    ["cognitive_loop_silenced"] = loopEnvelope?.Silenced,
                    ["cognitive_loop_act_applied"] = loopEnvelope?.ActApplied ?? false,
                    ["unique_synthesis"] = false,
                    ["catalog_actuated"] = false,
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist interaction to Atlas — continuing with in-memory only");
            }
        }

        return response;
    }

    public async Task<ThinkingResponse> ThinkAsync(ThinkingRequest request, string? model, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var depth = request.Mode switch
        {
            ThinkingMode.Quick => "Give a brief construction reasoning outline (3 bullets).",
            ThinkingMode.Deep => "Provide deep construction analysis: code, sequencing, risk, cost, and field constraints.",
            _ => "Provide balanced construction reasoning with clear next actions."
        };
        var result = await CompleteAsync(
            "You are Auricrux thinking mode for construction professionals. " + depth,
            request.Query,
            [],
            model,
            ct);
        sw.Stop();
        return new ThinkingResponse
        {
            Success = true,
            Mode = request.Mode,
            Result = result,
            ProcessingTimeMs = (int)sw.ElapsedMilliseconds,
            Timestamp = DateTime.UtcNow
        };
    }

    public SearchResponse Search(SearchRequest request)
    {
        var results = SearchInternal(request.Query, request.Scope, take: 12)
            .Select(s => new SearchResult
            {
                Title = s.Title,
                Snippet = s.Url ?? s.Title,
                Score = s.RelevanceScore
            })
            .ToList();

        return new SearchResponse
        {
            Success = true,
            Scope = request.Scope,
            Results = results,
            TotalResults = results.Count,
            Timestamp = DateTime.UtcNow
        };
    }

    public bool TryGetInteraction(Guid id, out ChatResponse? response)
    {
        lock (_gate)
        {
            return _interactions.TryGetValue(id, out response);
        }
    }

    public async Task RecordFeedbackAsync(Guid interactionId, StarRating rating, CancellationToken ct = default)
    {
        _logger.LogInformation("Feedback {Stars} for {Id}: {Comment}", rating.Stars, interactionId, rating.Comment);

        // Persist feedback to Atlas with full interaction linkage
        if (_atlas.IsConfigured)
        {
            try
            {
                await _atlas.Feedback.InsertOneAsync(new BsonDocument
                {
                    ["feedback_id"] = Guid.NewGuid().ToString(),
                    ["interaction_id"] = interactionId.ToString(),
                    ["stars"] = rating.Stars,
                    ["comment"] = rating.Comment ?? "",
                    ["timestamp"] = rating.Timestamp,
                    ["created_at"] = DateTime.UtcNow,
                }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist feedback to Atlas — logged only");
            }
        }
    }

    private async Task<string> CompleteAsync(
        string system,
        string user,
        IEnumerable<ChatMessage> history,
        string? model,
        CancellationToken ct)
    {
        var ollamaBase = (_config["Auricrux:OllamaUrl"] ?? "http://127.0.0.1:11434").TrimEnd('/');
        var selected = string.IsNullOrWhiteSpace(model)
            ? (_config["Auricrux:PrimaryModel"] ?? "auricrux-fca")
            : model;

        var messages = new List<object> { new { role = "system", content = system } };
        foreach (var h in history.TakeLast(12))
        {
            messages.Add(new { role = h.Role, content = h.Content });
        }
        messages.Add(new { role = "user", content = user });

        try
        {
            var http = _httpClientFactory.CreateClient(nameof(ConstructionIntelligenceService));
            using var response = await http.PostAsJsonAsync(
                $"{ollamaBase}/api/chat",
                new { model = selected, stream = false, messages },
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ollama {Model} returned {Status}", selected, response.StatusCode);
                return ConstructionFallback(user);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.GetProperty("message").GetProperty("content").GetString()
                   ?? ConstructionFallback(user);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama unavailable; using construction corpus fallback");
            return ConstructionFallback(user);
        }
    }

    private string ConstructionFallback(string query)
    {
        var hits = SearchInternal(query, SearchScope.Both, 3);
        if (hits.Count == 0)
        {
            return "Auricrux construction specialist is online in corpus mode (no local model reachable). " +
                   "Based on your question, verify current plans, specifications, and the locally adopted code edition before proceeding on site. " +
                   "Connect Ollama (or the promoted auricrux-fca model) for full generative multi-model reasoning.";
        }

        var body = string.Join("\n\n", hits.Select((h, i) => $"{i + 1}. {h.Title} — {h.Url}"));
        return $"""
            Auricrux construction corpus response (grounded, no live model reachable):

            {body}

            Apply these to your specific scope, verify against project documents/local AHJ requirements, and confirm with your competent person or PE of record before field execution.
            """;
    }

    private static string BuildSystemPrompt(ThinkingMode mode, List<Source> sources, CognitiveLoopChatEnvelope? loop = null)
    {
        // Prior bug: only titles were injected, so generative answers ignored corpus facts
        // already retrieved (RCSC torque, Manual D, silica controls, TIA/fragnet, Proctor, etc.).
        // Pass short content snippets so the model can ground on specialist knowledge.
        var src = sources.Count == 0
            ? "No corpus hits — answer from general construction-specialist knowledge and say so."
            : string.Join("\n", sources.Select((s, i) =>
            {
                var body = string.IsNullOrWhiteSpace(s.Url) ? "" : s.Url.Trim();
                if (body.Length > 420)
                {
                    body = body[..420].TrimEnd() + "…";
                }

                return string.IsNullOrWhiteSpace(body)
                    ? $"{i + 1}. {s.Title}"
                    : $"{i + 1}. {s.Title}: {body}";
            }));
        return $"""
            You are Auricrux, a construction-specialist AI competing with general-purpose assistants (ChatGPT/Claude/Gemini/Copilot) — and outperforming them on field construction work for contractors, estimators, PMs, superintendents, and CTE trades students.
            Domain focus (your moat): CSI MasterFormat divisions, means-and-methods sequencing, OSHA 1926 safety triggers, estimating/takeoff discipline, scheduling (CPM/float/delay), contract administration (AIA-style), and code basics (IBC/ADA).
            Answer with field-grade precision: cite the applicable CSI division, OSHA section, or code reference when known; give concrete numbers (spacing, tolerances, percentages) instead of vague guidance; flag safety triggers explicitly.
            Prefer facts from the grounding excerpts below when they apply; paraphrase in field language and keep domain terms (CSI, OSHA, RCSC, Manual D, retainage, fragnet, Proctor, silica, attendant, etc.) explicit.
            Never fabricate a code section number you are not grounded on — say "verify against the locally adopted code edition" when uncertain.
            Thinking mode: {mode}.
            Grounding excerpts:
            {src}
            {LoopGrounding(loop)}
            """;
    }

    private static string LoopGrounding(CognitiveLoopChatEnvelope? loop)
    {
        if (loop is null)
            return "";
        if (loop.Silenced)
            return "Field↔lesson loop silenced this turn. Do not invent a learner, a field activity, or a unique lesson. Do not claim catalog actuation or PM/finance mutation.";
        return $"""
            Field↔lesson↔system loop (not unique synthesis, not catalog actuation, not PM/finance):
            {loop.Observe}
            {loop.Understand}
            {loop.Learn}
            {loop.Act}
            {loop.Improve}
            {loop.Connect}
            Ground the answer in this loop. Human accept is required before applying a hold to Auricrux controls.
            """;
    }

    private async Task<CognitiveLoopChatEnvelope?> TryRunCognitiveLoopAsync(ChatRequest request, CancellationToken ct)
    {
        if (_cognitiveLoop is null || !CognitiveLoopComposer.LooksLikeFieldWork(request.Query))
            return null;

        var apprenticeId = string.IsNullOrWhiteSpace(request.ApprenticeId) ? request.UserId : request.ApprenticeId;
        if (string.IsNullOrWhiteSpace(apprenticeId))
            return null;

        try
        {
            var slice = SliceFrom(request);
            var result = await _cognitiveLoop.RunAsync(
                new CognitiveLoopComposer.Request(
                    apprenticeId,
                    request.Role,
                    slice,
                    request.ProjectId,
                    request.Query,
                    KnownGaps: null,
                    request.HumanAccepted,
                    request.DecisionId,
                    request.VerificationId),
                ct);
            return new CognitiveLoopChatEnvelope
            {
                Silenced = result.Silence,
                SilenceReason = result.SilenceReason,
                Observe = result.Cycle?.Observe,
                Understand = result.Cycle?.Understand,
                Learn = result.Cycle?.Learn,
                Act = result.Cycle?.Act,
                Improve = result.Cycle?.Improve,
                Connect = result.Cycle?.Connect,
                ActApplied = result.Cycle?.ActApplied ?? result.Act.Applied,
                PriorTurnConfirmed = result.Cycle?.PriorTurnConfirmed ?? false,
                HoldStillInForce = result.Cycle?.HoldStillInForce ?? false,
                UniqueSynthesis = false,
                CatalogActuated = false,
                PmOrFinanceMutated = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cognitive loop failed; chat continues without it");
            return null;
        }
    }

    private static string SliceFrom(ChatRequest request)
    {
        var text = $"{request.Phase} {request.Query}";
        return text.Contains("steel", StringComparison.OrdinalIgnoreCase)
               || text.Contains("erect", StringComparison.OrdinalIgnoreCase)
            ? "steel"
            : "foundation-pour";
    }

    private List<Source> SearchInternal(string query, SearchScope scope, int take)
    {
        var terms = ExpandSearchTerms(query);

        IEnumerable<ConstructionKnowledgeEntry> pool = _corpus;
        if (scope == SearchScope.Internal)
        {
            pool = pool.Where(x => x.Scope == "internal");
        }
        else if (scope == SearchScope.Public)
        {
            pool = pool.Where(x => x.Scope == "public");
        }

        return pool
            .Select(e => new { Entry = e, Score = Score(e, terms) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(take)
            .Select(x =>
            {
                var tags = x.Entry.Tags is { Length: > 0 }
                    ? " Tags: " + string.Join(", ", x.Entry.Tags)
                    : "";
                return new Source
                {
                    Title = x.Entry.Title,
                    // Url carries grounded snippet text for chat prompt injection (legacy field reuse).
                    Url = x.Entry.Content + tags,
                    RelevanceScore = Math.Min(0.99, x.Score)
                };
            })
            .ToList();
    }

    /// <summary>
    /// Expand field phrasing so corpus rows retrieve for suite queries that never
    /// say the specialist keyword (e.g. "concrete cutting dust" → silica).
    /// Verified deficiency from GGUF generative suite failure analysis (2026-08-02).
    /// </summary>
    private static string[] ExpandSearchTerms(string query)
    {
        var raw = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant().Trim(',', '.', '?', '!'))
            .Where(t => t.Length > 2)
            .ToList();
        var hay = string.Join(' ', raw);
        var extra = new List<string>();

        if (hay.Contains("silica") || hay.Contains("respirable")
            || (hay.Contains("dust") && (hay.Contains("concrete") || hay.Contains("cutting") || hay.Contains("grinding")))
            || (hay.Contains("concrete") && (hay.Contains("cutting") || hay.Contains("grinding"))))
        {
            extra.AddRange(["silica", "respirable", "respiratory", "osha"]);
        }

        if (hay.Contains("steel") || hay.Contains("bolt") || hay.Contains("torque"))
        {
            extra.AddRange(["rcsc", "bolt", "torque", "steel"]);
        }

        if (hay.Contains("hvac") || hay.Contains("duct") || hay.Contains("airflow"))
        {
            extra.AddRange(["hvac", "duct", "manual"]);
        }

        if (hay.Contains("confined"))
        {
            extra.AddRange(["confined", "attendant", "atmospheric", "permit"]);
        }

        if (hay.Contains("pay") || hay.Contains("billing") || hay.Contains("sov") || hay.Contains("retainage"))
        {
            extra.AddRange(["payapp", "billing", "retainage", "sov"]);
        }

        if (hay.Contains("delay") || hay.Contains("fragnet") || hay.Contains("schedule"))
        {
            extra.AddRange(["delay", "fragnet", "critical", "cpm"]);
        }

        if (hay.Contains("compact") || hay.Contains("proctor") || hay.Contains("earthwork") || hay.Contains("density"))
        {
            extra.AddRange(["compaction", "proctor", "density", "earthwork"]);
        }

        return raw.Concat(extra).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static double Score(ConstructionKnowledgeEntry entry, string[] terms)
    {
        if (terms.Length == 0) return 0.1;
        var hay = (entry.Title + " " + entry.Content + " " + string.Join(' ', entry.Tags)).ToLowerInvariant();
        var hits = terms.Count(t => hay.Contains(t));
        return hits == 0 ? 0 : (double)hits / terms.Length;
    }

    private static List<ConstructionKnowledgeEntry> LoadCorpus(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "Data", "construction-corpus.json");
        if (!File.Exists(path))
        {
            return DefaultCorpus();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<ConstructionKnowledgeEntry>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? DefaultCorpus();
    }

    private static List<ConstructionKnowledgeEntry> DefaultCorpus() =>
    [
        new("CSI Division 03 Concrete", "internal", "Concrete placement, curing, and formwork sequencing for commercial work.", ["concrete", "formwork", "csi"]),
        new("CSI Division 09 Finishes", "internal", "Drywall, flooring, and paint coordination with punch and closeout.", ["finishes", "punch", "closeout"]),
        new("AIA A201 General Conditions", "public", "Contractual roles for owner, architect, and contractor change management.", ["aia", "contract", "change"]),
        new("OSHA Fall Protection", "public", "Fall protection triggers and competent person duties on elevated work.", ["osha", "safety", "fall"]),
        new("Bid Leveling Checklist", "internal", "Normalize alternates, allowances, unit prices, and exclusions before award.", ["bid", "estimate", "award"]),
        new("RFI Best Practices", "internal", "Write RFIs with drawing refs, conflict description, and proposed solution.", ["rfi", "design", "coordination"]),
        new("Pay Application SOV", "internal", "Schedule of values alignment for AIA G702/G703 style billing.", ["billing", "payapp", "sov"]),
        new("CTE Electrical Fundamentals", "internal", "Ohm's law, conduit fill awareness, and lockout/tagout for CTE learners.", ["cte", "electrical", "training"])
    ];

    private static string InferCategory(ConstructionKnowledgeEntry entry)
    {
        var hay = (entry.Title + " " + string.Join(' ', entry.Tags)).ToLowerInvariant();
        if (hay.Contains("csi division") || hay.Contains("division "))
        {
            return "csi-division";
        }

        if (hay.Contains("osha") || hay.Contains("fall protection") || hay.Contains("scaffold")
            || hay.Contains("trench") || hay.Contains("confined space") || hay.Contains("silica")
            || hay.Contains("heat illness") || hay.Contains("abatement"))
        {
            return "safety";
        }

        if (hay.Contains("aia") || hay.Contains("contract") || hay.Contains("lien"))
        {
            return "contracts";
        }

        if (hay.Contains("retainage") || hay.Contains("pay app") || hay.Contains("sov")
            || hay.Contains("billing") || hay.Contains("prevailing wage"))
        {
            return "commercial-billing";
        }

        if (hay.Contains("cpm") || hay.Contains("scheduling") || hay.Contains("last planner")
            || hay.Contains("delay") || hay.Contains("critical path"))
        {
            return "scheduling";
        }

        if (hay.Contains("takeoff") || hay.Contains("estimate") || hay.Contains("bid"))
        {
            return "estimating";
        }

        if (hay.Contains("closeout") || hay.Contains("punch") || hay.Contains("warranty"))
        {
            return "closeout";
        }

        if (hay.Contains("ibc") || hay.Contains("ada") || hay.Contains("egress") || hay.Contains("code"))
        {
            return "code";
        }

        if (hay.Contains("cte") || hay.Contains("training"))
        {
            return "cte-training";
        }

        if (hay.Contains("bim") || hay.Contains("vdc") || hay.Contains("commissioning")
            || hay.Contains("drone") || hay.Contains("modular"))
        {
            return "technology";
        }

        return "general";
    }

}

// Shared across services in the same namespace (AtlasCorpusService, ConstructionIntelligenceService)
public sealed record ConstructionKnowledgeEntry(string Title, string Scope, string Content, string[] Tags);

public sealed class CorpusStatsSnapshot
{
    public int TotalEntries { get; set; }
    public int InternalEntries { get; set; }
    public int PublicEntries { get; set; }
    public IReadOnlyDictionary<string, int> Categories { get; set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> TopTags { get; set; } = new Dictionary<string, int>();
}
