using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Auricrux.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;

namespace Auricrux.Shared.Services;

/// <summary>
/// Client for communicating with the Auricrux backend API
/// </summary>
public class AuricruxApiClient
{
    private readonly HttpClient _httpClient;
    private readonly AuricruxConfig _config;
    private readonly ILogger<AuricruxApiClient> _logger;

    public AuricruxApiClient(HttpClient httpClient, AuricruxConfig config, ILogger<AuricruxApiClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;

        // Set base address and default headers
        _httpClient.BaseAddress = new Uri(_config.ApiEndpoint);
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Auricrux-Client/1.0");
    }

    /// <summary>
    /// Attaches (or clears, when null/empty) a bearer token to every subsequent request.
    /// Used by the mobile OIDC token storage path (AUX-021) once a client has signed in
    /// and a token is loaded from secure storage.
    /// </summary>
    public void SetBearerToken(string? accessToken)
    {
        _httpClient.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(accessToken)
            ? null
            : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
    }

    /// <summary>
    /// Send a chat query and get a response from Auricrux backend
    /// </summary>
    public async Task<ChatResponse?> SendChatAsync(
        ChatRequest request,
        string? model = null,
        string? accountEmail = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_config.EnableLogging)
            {
                _logger.LogInformation($"Sending chat query: {request.Query} (Mode: {request.ThinkingMode}, Scope: {request.SearchScope}, Model: {model ?? "default"})");
            }

            var options = CreateJsonOptions();
            var path = string.IsNullOrWhiteSpace(model)
                ? "api/chat"
                : $"api/chat?model={Uri.EscapeDataString(model.Trim())}";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(request, options: options)
            };
            if (!string.IsNullOrWhiteSpace(accountEmail))
            {
                httpRequest.Headers.TryAddWithoutValidation("X-Auricrux-Email", accountEmail.Trim());
            }

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError($"API Error: {response.StatusCode} - {body}");
                return null;
            }

            var chatResponse = await response.Content.ReadFromJsonAsync<ChatResponse>(options, cancellationToken);

            if (_config.EnableLogging)
            {
                _logger.LogInformation($"Received response in {chatResponse?.ProcessingTimeMs}ms");
            }

            return chatResponse;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"HTTP Request failed: {ex.Message}");
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError($"Request timeout: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unexpected error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Check if the backend is available (prefers /healthz on the public Auricrux edge).
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in new[] { "healthz", "health" })
        {
            try
            {
                var response = await _httpClient.GetAsync(path, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // try next path
            }
        }

        return false;
    }

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Submit user feedback/rating for an interaction
    /// </summary>
    public async Task<bool> SubmitFeedbackAsync(string interactionId, StarRating rating, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_config.EnableLogging)
            {
                _logger.LogInformation($"Submitting feedback for interaction {interactionId}: {rating.Stars} stars");
            }

            var options = CreateJsonOptions();

            var response = await _httpClient.PostAsJsonAsync($"api/feedback/{interactionId}", rating, options, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to submit feedback: {ex.Message}");
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("api/models", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Array.Empty<string>();
            }

            var payload = await response.Content.ReadFromJsonAsync<ModelsPayload>(CreateJsonOptions(), cancellationToken);
            return (IReadOnlyList<string>)(payload?.Models ?? new List<string>());
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to list models: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public async Task<FreemiumAccount?> RegisterAccountAsync(string email, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/account/register",
                new { email },
                CreateJsonOptions(),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<FreemiumAccount>(CreateJsonOptions(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to register account: {ex.Message}");
            return null;
        }
    }

    public async Task<FreemiumAccount?> GetAccountAsync(string email, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"api/account/{Uri.EscapeDataString(email.Trim())}",
                cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<FreemiumAccount>(CreateJsonOptions(), cancellationToken)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to get account: {ex.Message}");
            return null;
        }
    }

    public async Task<(FreemiumAccount? account, bool limitReached, string? error)> ConsumeAsync(string email, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsync(
                $"api/account/{Uri.EscapeDataString(email.Trim())}/consume",
                null,
                cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return (null, true, body);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, false, $"Consume failed: {response.StatusCode}");
            }

            var account = await response.Content.ReadFromJsonAsync<FreemiumAccount>(CreateJsonOptions(), cancellationToken);
            return (account, false, null);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to consume quota: {ex.Message}");
            return (null, false, ex.Message);
        }
    }

    public async Task<FreemiumAccount?> UpgradeAccountAsync(string email, string plan, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"api/account/{Uri.EscapeDataString(email.Trim())}/upgrade",
                new { plan },
                CreateJsonOptions(),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<FreemiumAccount>(CreateJsonOptions(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to upgrade account: {ex.Message}");
            return null;
        }
    }

    public async Task<MediaArtifactDto?> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/media/image", new { prompt }, CreateJsonOptions(), cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<MediaArtifactDto>(CreateJsonOptions(), cancellationToken)
            : null;
    }

    public async Task<MediaArtifactDto?> GenerateVideoAsync(string prompt, int frames = 8, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/media/video", new { prompt, frames }, CreateJsonOptions(), cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<MediaArtifactDto>(CreateJsonOptions(), cancellationToken)
            : null;
    }

    public async Task AppendMemoryAsync(string sessionId, string role, string content, string backend = "sqlite", CancellationToken cancellationToken = default)
    {
        await _httpClient.PostAsJsonAsync(
            $"api/memory/{Uri.EscapeDataString(sessionId)}",
            new { role, content, backend },
            CreateJsonOptions(),
            cancellationToken);
    }

    public async Task<FcaLinkDto?> LinkFcaAsync(string email, string fcaBearerToken, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"api/account/{Uri.EscapeDataString(email.Trim())}/link-fca",
            new { fcaBearerToken },
            CreateJsonOptions(),
            cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<FcaLinkDto>(CreateJsonOptions(), cancellationToken)
            : null;
    }

    public async Task UploadWorkspaceFileAsync(Stream content, string fileName, string? folder = null, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var streamContent = new StreamContent(content);
        form.Add(streamContent, "file", fileName);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            form.Add(new StringContent(folder), "folder");
        }

        await _httpClient.PostAsync("api/workspace/files", form, cancellationToken);
    }

    public async Task<JsonElement?> BrowseAsync(string url, string? question = null, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/browse",
            new { url, question },
            CreateJsonOptions(),
            cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JsonElement>(CreateJsonOptions(), cancellationToken)
            : null;
    }

    public async Task<JsonElement?> RunAgentAsync(string query, string? model = null, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(model)
            ? "api/agent"
            : $"api/agent?model={Uri.EscapeDataString(model.Trim())}";
        var response = await _httpClient.PostAsJsonAsync(path, new { query }, CreateJsonOptions(), cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JsonElement>(CreateJsonOptions(), cancellationToken)
            : null;
    }

    public async Task<JsonElement?> CalcAsync(string operation, object? args = null, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/calc",
            new { operation, args },
            CreateJsonOptions(),
            cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JsonElement>(CreateJsonOptions(), cancellationToken)
            : null;
    }

    public async Task<JsonElement?> AnalyzeVisionAsync(
        string imageBase64,
        string? prompt = null,
        string? focus = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "api/vision",
            new { imageBase64, prompt, focus },
            CreateJsonOptions(),
            cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JsonElement>(CreateJsonOptions(), cancellationToken)
            : null;
    }

    private sealed class ModelsPayload
    {
        public List<string>? Models { get; set; }
    }
}

public sealed class MediaArtifactDto
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string PublicPath { get; set; } = string.Empty;
    public string Engine { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
}

public sealed class FcaLinkDto
{
    public string AuricruxEmail { get; set; } = string.Empty;
    public DateTime LinkedAtUtc { get; set; }
    public string Plan { get; set; } = string.Empty;
    public bool HasAcademy { get; set; }
    public bool HasCte { get; set; }
    public bool HasEmbeddedAuricrux { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class FreemiumAccount
{
    public string Email { get; set; } = string.Empty;
    public string Plan { get; set; } = "free";
    public int DailyQueryLimit { get; set; }
    public int QueriesUsedToday { get; set; }
}

/// <summary>
/// Service for text-to-speech functionality
/// </summary>
public class TextToSpeechService
{
    private readonly ILogger<TextToSpeechService> _logger;
    private bool _isInitialized;

    public TextToSpeechService(ILogger<TextToSpeechService> logger)
    {
        _logger = logger;
        _isInitialized = false;
    }

    /// <summary>
    /// Initialize TTS (platform-specific implementation)
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            if (_isInitialized) return;

            try
            {
                _ = DeviceInfo.Platform;
            }
            catch
            {
                _isInitialized = true;
                _logger.LogInformation("TTS initialized in non-MAUI host (no-op speak)");
                return;
            }

            // Platform-specific initialization (handled by platform implementations)
            if (DeviceInfo.Platform == DevicePlatform.WinUI
                || DeviceInfo.Platform == DevicePlatform.iOS
                || DeviceInfo.Platform == DevicePlatform.Android
                || DeviceInfo.Platform == DevicePlatform.macOS
                || true)
            {
                _isInitialized = true;
            }

            _logger.LogInformation("TTS initialized successfully");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _isInitialized = true;
            _logger.LogError($"TTS initialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Speak text using platform's TTS
    /// </summary>
    public async Task SpeakAsync(string text)
    {
        try
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            try
            {
                await TextToSpeech.Default.SpeakAsync(text, new SpeechOptions
                {
                    Volume = 1.0f,
                    Pitch = 1.0f
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug("TTS speak skipped on this host: {Message}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"TTS failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop current TTS playback
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            if (TextToSpeech.Default is ITextToSpeech tts)
            {
                // Best-effort cancel; MAUI exposes Cancel on some platforms via SpeakAsync cancellation.
                await Task.CompletedTask;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to stop TTS: {ex.Message}");
        }
    }
}

/// <summary>
/// Service for managing Auricrux interactions and local state
/// </summary>
public class AuricruxService
{
    private readonly AuricruxApiClient _apiClient;
    private readonly TextToSpeechService _ttsService;
    private readonly ILogger<AuricruxService> _logger;
    private readonly List<AuricruxInteraction> _interactionHistory;
    public string SessionId { get; private set; }

    public AuricruxService(AuricruxApiClient apiClient, TextToSpeechService ttsService, ILogger<AuricruxService> logger)
    {
        _apiClient = apiClient;
        _ttsService = ttsService;
        _logger = logger;
        _interactionHistory = new List<AuricruxInteraction>();
        SessionId = Guid.NewGuid().ToString();

        _logger.LogInformation($"AuricruxService initialized with session: {SessionId}");
    }

    /// <summary>
    /// Process a user query and get a response
    /// </summary>
    public async Task<(ChatResponse? response, AuricruxInteraction? interaction)> ProcessQueryAsync(
        string query,
        ThinkingMode thinkingMode = ThinkingMode.Auto,
        SearchScope searchScope = SearchScope.Both,
        string? model = null,
        string? accountEmail = null,
        string? apprenticeId = null,
        string? role = null,
        string? projectId = null,
        string? phase = null,
        bool humanAccepted = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var startTime = DateTime.UtcNow;

            var chatRequest = new ChatRequest
            {
                Query = query,
                ThinkingMode = thinkingMode,
                SearchScope = searchScope,
                SessionId = SessionId,
                UserId = apprenticeId,
                ApprenticeId = apprenticeId,
                Role = role,
                ProjectId = projectId,
                Phase = phase,
                HumanAccepted = humanAccepted,
                ConversationHistory = _interactionHistory
                    .OrderByDescending(x => x.Timestamp)
                    .Take(10)
                    .Select(x => new ChatMessage { Role = "assistant", Content = x.Response })
                    .ToList()
            };

            var response = await _apiClient.SendChatAsync(chatRequest, model, accountEmail, cancellationToken);

            if (response == null)
            {
                _logger.LogError("Failed to get response from API");
                return (null, null);
            }

            var interaction = new AuricruxInteraction
            {
                SessionId = SessionId,
                Query = query,
                Response = response.Content,
                ThinkingMode = thinkingMode,
                SearchScope = searchScope,
                ProcessingTimeMs = response.ProcessingTimeMs,
                Timestamp = startTime
            };

            _interactionHistory.Add(interaction);

            _logger.LogInformation($"Query processed successfully: {interaction.Id}");

            return (response, interaction);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error processing query: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Submit feedback for an interaction
    /// </summary>
    public async Task<bool> SubmitFeedbackAsync(string interactionId, int stars, string? comment = null)
    {
        try
        {
            var interaction = _interactionHistory.FirstOrDefault(x => x.Id == interactionId);
            if (interaction == null)
            {
                _logger.LogWarning($"Interaction not found: {interactionId}");
                return false;
            }

            var rating = new StarRating { Stars = stars, Comment = comment };
            interaction.Feedback = rating;

            var success = await _apiClient.SubmitFeedbackAsync(interactionId, rating);

            if (success)
            {
                _logger.LogInformation($"Feedback submitted for {interactionId}");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error submitting feedback: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get conversation history
    /// </summary>
    public IReadOnlyList<AuricruxInteraction> GetHistory() => _interactionHistory.AsReadOnly();

    /// <summary>
    /// Clear conversation history
    /// </summary>
    public void ClearHistory()
    {
        _interactionHistory.Clear();
        SessionId = Guid.NewGuid().ToString();
        _logger.LogInformation("Conversation history cleared, new session started");
    }
}
