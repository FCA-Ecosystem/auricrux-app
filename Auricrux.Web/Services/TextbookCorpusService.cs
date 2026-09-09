using System.Text.RegularExpressions;
using Auricrux.Shared.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Auricrux.Web.Services;

/// <summary>
/// Live retrieval of Claude-authored Academy textbook chunks from Atlas <c>chunks</c>.
/// Connect only: does not actuate Academy catalog, CTE credentials, or PM/finance.
/// </summary>
public sealed class TextbookCorpusService
{
    public const string Domain = "academy-textbook";

    private readonly AtlasService _atlas;
    private readonly ILogger<TextbookCorpusService> _logger;

    public TextbookCorpusService(AtlasService atlas, ILogger<TextbookCorpusService> logger)
    {
        _atlas = atlas;
        _logger = logger;
    }

    public bool IsAtlasActive => _atlas.IsConfigured;

    public async Task<TextbookCorpusStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (!_atlas.IsConfigured)
            return new TextbookCorpusStatus(false, "not_configured", 0);
        try
        {
            var count = await _atlas.Chunks.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.Eq("domain", Domain),
                cancellationToken: ct);
            return new TextbookCorpusStatus(true, "ok", count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Textbook corpus count failed");
            return new TextbookCorpusStatus(true, "unreachable", 0);
        }
    }

    public async Task<IReadOnlyList<TextbookCorpusHit>> SearchAsync(
        string query, int take = 5, CancellationToken ct = default)
    {
        if (!_atlas.IsConfigured || string.IsNullOrWhiteSpace(query))
            return [];

        take = Math.Clamp(take, 1, 10);
        try
        {
            var hits = await SearchWithAtlasIndexAsync(query.Trim(), take, ct);
            if (hits.Count > 0)
                return hits;
            return await FallbackRegexSearchAsync(query.Trim(), take, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Textbook corpus search failed");
            return await FallbackRegexSearchAsync(query.Trim(), take, ct);
        }
    }

    public async Task<List<Source>> SearchAsSourcesAsync(
        string query, int take = 3, CancellationToken ct = default)
    {
        var hits = await SearchAsync(query, take, ct);
        return hits.Select(h => new Source
        {
            Title = $"Academy textbook · {h.ProgramTitle}: {h.SectionHeading}",
            Url = h.Snippet,
            RelevanceScore = Math.Min(0.99, h.Score <= 0 ? 0.55 : Math.Min(0.99, h.Score * 0.15))
        }).ToList();
    }

    private async Task<IReadOnlyList<TextbookCorpusHit>> SearchWithAtlasIndexAsync(
        string query, int take, CancellationToken ct)
    {
        var searchStage = new BsonDocument
        {
            ["$search"] = new BsonDocument
            {
                ["index"] = "chunks_text_search",
                ["compound"] = new BsonDocument
                {
                    ["must"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["text"] = new BsonDocument
                            {
                                ["query"] = query,
                                ["path"] = new BsonArray { "text", "section", "source" },
                                ["fuzzy"] = new BsonDocument { ["maxEdits"] = 1 }
                            }
                        }
                    },
                    ["filter"] = new BsonArray
                    {
                        new BsonDocument
                        {
                            ["equals"] = new BsonDocument
                            {
                                ["path"] = "domain",
                                ["value"] = Domain
                            }
                        }
                    }
                }
            }
        };
        var pipeline = new[]
        {
            searchStage,
            new BsonDocument { ["$addFields"] = new BsonDocument { ["_score"] = new BsonDocument { ["$meta"] = "searchScore" } } },
            new BsonDocument { ["$limit"] = take }
        };

        try
        {
            var cursor = await _atlas.Chunks.AggregateAsync<BsonDocument>(pipeline, cancellationToken: ct);
            var docs = await cursor.ToListAsync(ct);
            return docs.Select(d => ToHit(d)).ToList();
        }
        catch (MongoCommandException ex) when (ex.Message.Contains("chunks_text_search"))
        {
            _logger.LogWarning("Atlas Search index chunks_text_search unavailable for textbooks — regex fallback");
            return await FallbackRegexSearchAsync(query, take, ct);
        }
    }

    private async Task<IReadOnlyList<TextbookCorpusHit>> FallbackRegexSearchAsync(
        string query, int take, CancellationToken ct)
    {
        var escaped = Regex.Escape(query);
        var textFilter = Builders<BsonDocument>.Filter.Regex("text", new BsonRegularExpression(escaped, "i"));
        var sectionFilter = Builders<BsonDocument>.Filter.Regex("section", new BsonRegularExpression(escaped, "i"));
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("domain", Domain),
            Builders<BsonDocument>.Filter.Or(textFilter, sectionFilter));
        var docs = await _atlas.Chunks.Find(filter).Limit(take).ToListAsync(ct);
        return docs.Select(d => ToHit(d, 0.5)).ToList();
    }

    private static TextbookCorpusHit ToHit(BsonDocument doc, double? scoreOverride = null)
    {
        var metadata = doc.Contains("metadata") && doc["metadata"].IsBsonDocument
            ? doc["metadata"].AsBsonDocument
            : new BsonDocument();
        var text = doc.GetValue("text", "").AsString;
        var snippet = text.Length <= 280 ? text : text[..280] + "…";
        var score = scoreOverride
            ?? (doc.Contains("_score") && !doc["_score"].IsBsonNull ? doc["_score"].ToDouble() : 0.5);
        return new TextbookCorpusHit(
            doc["_id"].ToString() ?? "",
            metadata.GetValue("program", "").AsString,
            metadata.GetValue("programTitle", "").AsString,
            doc.GetValue("section", "").AsString,
            snippet,
            metadata.GetValue("provenanceNotice", "").AsString,
            score);
    }
}

public sealed record TextbookCorpusStatus(bool Configured, string Status, long Count);

public sealed record TextbookCorpusHit(
    string Id,
    string Program,
    string ProgramTitle,
    string SectionHeading,
    string Snippet,
    string ProvenanceNotice,
    double Score);
