using System.Net.Http.Json;
using System.Text.Json;

namespace Auricrux.Web.Services;

public sealed class BackendHealthReport
{
    public string Status { get; set; } = "unknown";
    public string App { get; set; } = "Auricrux";
    public string Version { get; set; } = "1.0.0";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool OllamaReachable { get; set; }
    public bool PrimaryModelReady { get; set; }
    public string PrimaryModel { get; set; } = string.Empty;
    public int CorpusEntries { get; set; }
    public IReadOnlyList<string> MemoryBackends { get; set; } = [];
    public IReadOnlyList<string> Models { get; set; } = [];
    public string RuntimeMode { get; set; } = "corpus-fallback";
    public bool AtlasConfigured { get; set; }
    public string AtlasStatus { get; set; } = "not_configured";
    public PackageIdentitySnapshot? PackageIdentity { get; set; }
    public string GitSha { get; set; } = "";
}

public sealed class BackendHealthService(
    ConstructionIntelligenceService intelligence,
    ConversationMemoryService memory,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    PackageIdentityService packageIdentity,
    AtlasService atlas,
    ILogger<BackendHealthService> logger)
{
    public async Task<BackendHealthReport> ProbeAsync(CancellationToken ct = default)
    {
        var primary = config["Auricrux:PrimaryModel"] ?? "llama3.2";
        var ollamaBase = (config["Auricrux:OllamaUrl"] ?? "http://127.0.0.1:11434").TrimEnd('/');
        var pkg = packageIdentity.GetIdentity();
        var report = new BackendHealthReport
        {
            PrimaryModel = primary,
            CorpusEntries = intelligence.CorpusEntryCount,
            MemoryBackends = memory.Backends,
            Models = intelligence.AvailableModels,
            Timestamp = DateTime.UtcNow,
            Version = string.IsNullOrWhiteSpace(pkg.PackageVersion) ? "1.4.0" : pkg.PackageVersion,
            PackageIdentity = pkg,
            GitSha = pkg.GitSha ?? ""
        };

        report.AtlasConfigured = atlas.IsConfigured;
        if (!atlas.IsConfigured)
        {
            report.AtlasStatus = "not_configured";
        }
        else
        {
            var (ok, _) = await atlas.PingAsync(ct);
            report.AtlasStatus = ok ? "ok" : "unreachable";
        }

        try
        {
            var http = httpClientFactory.CreateClient(nameof(BackendHealthService));
            using var response = await http.GetAsync($"{ollamaBase}/api/tags", ct);
            report.OllamaReachable = response.IsSuccessStatusCode;
            if (report.OllamaReachable)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var names = doc.RootElement.GetProperty("models").EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString() ?? string.Empty)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                report.PrimaryModelReady = names.Any(n =>
                    n.Equals(primary, StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith(primary + ":", StringComparison.OrdinalIgnoreCase));
                report.RuntimeMode = report.PrimaryModelReady ? "ollama-live" : "ollama-degraded";
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ollama health probe failed");
            report.OllamaReachable = false;
        }

        report.Status = report.CorpusEntries == 0
            ? "unhealthy"
            : report.OllamaReachable && report.PrimaryModelReady
                ? "healthy"
                : "degraded";

        return report;
    }
}
