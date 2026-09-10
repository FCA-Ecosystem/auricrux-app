namespace Auricrux.Shared.Models;

/// <summary>
/// Thinking mode for LLM responses
/// </summary>
public enum ThinkingMode
{
    /// <summary>Quick response with minimal reasoning</summary>
    Quick,
    
    /// <summary>Balanced thinking and response time</summary>
    Auto,
    
    /// <summary>Deep analysis with extended thinking</summary>
    Deep
}

/// <summary>
/// Search scope for queries
/// </summary>
public enum SearchScope
{
    /// <summary>Search only internal documents</summary>
    Internal,
    
    /// <summary>Search public resources</summary>
    Public,
    
    /// <summary>Search both internal and public</summary>
    Both
}

/// <summary>
/// User feedback rating for chat interactions
/// </summary>
public class StarRating
{
    /// <summary>Rating from 1 to 5</summary>
    public int Stars { get; set; }

    /// <summary>Optional comment from user</summary>
    public string? Comment { get; set; }

    /// <summary>Timestamp of feedback</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Request sent to the Auricrux backend
/// </summary>
public class ChatRequest
{
    /// <summary>User's query or message</summary>
    public required string Query { get; set; }

    /// <summary>Thinking mode to use for the response</summary>
    public ThinkingMode ThinkingMode { get; set; } = ThinkingMode.Auto;

    /// <summary>Search scope for the query</summary>
    public SearchScope SearchScope { get; set; } = SearchScope.Both;

    /// <summary>Conversation history for context</summary>
    public List<ChatMessage> ConversationHistory { get; set; } = new();

    /// <summary>Session identifier for tracking</summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString();

    // ── Context parameters (Phase 6 + Phase 8 FCA ecosystem integration) ────────

    /// <summary>User ID for personalized context (legacy string support)</summary>
    public string? UserId { get; set; }

    /// <summary>Project ID for project-specific context (legacy string support)</summary>
    public string? ProjectId { get; set; }

    /// <summary>User role (e.g., "Project Manager", "Superintendent", "Foreman")</summary>
    public string? Role { get; set; }

    /// <summary>Current construction phase (e.g., "preconstruction", "foundations", "framing")</summary>
    public string? Phase { get; set; }

    /// <summary>Apprentice id for the field↔lesson↔system loop. Falls back to UserId.</summary>
    public string? ApprenticeId { get; set; }

    /// <summary>When true and proof ids are present, apply the job-derived act to Auricrux controls. Default false.</summary>
    public bool HumanAccepted { get; set; }

    /// <summary>Closed-loop decision id used only when HumanAccepted is true.</summary>
    public string? DecisionId { get; set; }

    /// <summary>Closed-loop verification id used only when HumanAccepted is true.</summary>
    public string? VerificationId { get; set; }
    
    // ── Phase 8: Typed FCA domain references (preferred) ─────────────────────────
    
    /// <summary>FCA Member ID (typed reference to FCA ecosystem)</summary>
    public Guid? MemberId { get; set; }
    
    /// <summary>FCA Project ID (typed reference to FCA ecosystem)</summary>
    public Guid? FcaProjectId { get; set; }
    
    /// <summary>FCA Role Name (one of: Admin, PM, Field, Owner, Accountant)</summary>
    public string? FcaRoleName { get; set; }
}

/// <summary>
/// Response from the Auricrux backend
/// </summary>
public class ChatResponse
{
    /// <summary>The AI-generated response</summary>
    public required string Content { get; set; }

    /// <summary>Thinking process (if available)</summary>
    public string? ThinkingContent { get; set; }

    /// <summary>Sources used for the response</summary>
    public List<Source> Sources { get; set; } = new();

    /// <summary>Response timestamp</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Duration of processing in milliseconds</summary>
    public long ProcessingTimeMs { get; set; }

    /// <summary>Confidence score (0-1)</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>Server interaction id when provided</summary>
    public Guid? InteractionId { get; set; }

    /// <summary>
    /// Field↔lesson↔system turn grounded in this query. Null when the query is not field work
    /// or apprentice identity is missing. Not unique synthesis. Not catalog actuation.
    /// </summary>
    public CognitiveLoopChatEnvelope? CognitiveLoop { get; set; }
}

/// <summary>Chat-visible Observe→Improve envelope. Not unique synthesis.</summary>
public class CognitiveLoopChatEnvelope
{
    public bool Silenced { get; set; }
    public string? SilenceReason { get; set; }
    public string? Observe { get; set; }
    public string? Understand { get; set; }
    public string? Learn { get; set; }
    public string? Act { get; set; }
    public string? Improve { get; set; }
    public string? Connect { get; set; }
    public bool ActApplied { get; set; }
    public bool PriorTurnConfirmed { get; set; }
    public bool HoldStillInForce { get; set; }
    public bool UniqueSynthesis { get; set; }
    public bool CatalogActuated { get; set; }
    public bool PmOrFinanceMutated { get; set; }
}

/// <summary>
/// Single message in conversation history
/// </summary>
public class ChatMessage
{
    /// <summary>Message role (user or assistant)</summary>
    public required string Role { get; set; }

    /// <summary>Message content</summary>
    public required string Content { get; set; }

    /// <summary>Timestamp of the message</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Source reference for a response
/// </summary>
public class Source
{
    /// <summary>Title or name of the source</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>URL or identifier for the source</summary>
    public string? Url { get; set; }

    /// <summary>Relevance score (0-1)</summary>
    public double RelevanceScore { get; set; }
}

/// <summary>
/// Auricrux interaction record for tracking and analytics
/// </summary>
public class AuricruxInteraction
{
    /// <summary>Unique identifier for this interaction</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Session identifier</summary>
    public required string SessionId { get; set; }

    /// <summary>The original query</summary>
    public required string Query { get; set; }

    /// <summary>The response from Auricrux</summary>
    public required string Response { get; set; }

    /// <summary>Thinking mode used</summary>
    public ThinkingMode ThinkingMode { get; set; }

    /// <summary>Search scope used</summary>
    public SearchScope SearchScope { get; set; }

    /// <summary>User's star rating feedback</summary>
    public StarRating? Feedback { get; set; }

    /// <summary>Timestamp of interaction</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Processing time in milliseconds</summary>
    public long ProcessingTimeMs { get; set; }
}

/// <summary>
/// Configuration for the Auricrux client
/// </summary>
public class AuricruxConfig
{
    /// <summary>Backend API endpoint</summary>
    public string ApiEndpoint { get; set; } = "https://auricrux.futurecontractorsofamerica.com";

    /// <summary>Default thinking mode</summary>
    public ThinkingMode DefaultThinkingMode { get; set; } = ThinkingMode.Auto;

    /// <summary>Default search scope</summary>
    public SearchScope DefaultSearchScope { get; set; } = SearchScope.Both;

    /// <summary>Enable audio/TTS by default</summary>
    public bool EnableAutoSpeak { get; set; } = false;

    /// <summary>API timeout in seconds</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Enable request logging</summary>
    public bool EnableLogging { get; set; } = true;
}

/// <summary>
/// Request for thinking mode operations
/// </summary>
public class ThinkingRequest
{
    /// <summary>User's query for thinking</summary>
    public required string Query { get; set; }

    /// <summary>Thinking mode to use</summary>
    public ThinkingMode Mode { get; set; } = ThinkingMode.Auto;
}

/// <summary>
/// Response from thinking mode operation
/// </summary>
public class ThinkingResponse
{
    /// <summary>Success status</summary>
    public bool Success { get; set; }

    /// <summary>Thinking mode used</summary>
    public ThinkingMode Mode { get; set; }

    /// <summary>Thinking result</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>Processing time in milliseconds</summary>
    public int ProcessingTimeMs { get; set; }

    /// <summary>Response timestamp</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Request for search operations
/// </summary>
public class SearchRequest
{
    /// <summary>Search query</summary>
    public required string Query { get; set; }

    /// <summary>Search scope</summary>
    public SearchScope Scope { get; set; } = SearchScope.Both;
}

/// <summary>
/// Single search result
/// </summary>
public class SearchResult
{
    /// <summary>Result title</summary>
    public required string Title { get; set; }

    /// <summary>Result snippet/summary</summary>
    public string Snippet { get; set; } = string.Empty;

    /// <summary>Relevance score (0-1)</summary>
    public double Score { get; set; }
}

/// <summary>
/// Response from search operation
/// </summary>
public class SearchResponse
{
    /// <summary>Success status</summary>
    public bool Success { get; set; }

    /// <summary>Search scope used</summary>
    public SearchScope Scope { get; set; }

    /// <summary>Search results</summary>
    public List<SearchResult> Results { get; set; } = new();

    /// <summary>Total number of results</summary>
    public int TotalResults { get; set; }

    /// <summary>Response timestamp</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
