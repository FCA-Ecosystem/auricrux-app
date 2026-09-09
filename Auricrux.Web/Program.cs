using System.Threading.RateLimiting;
using Auricrux.Shared.Models;
using Auricrux.Shared.Services;
using Auricrux.Web.Components;
using Auricrux.Web.Middleware;
using Auricrux.Web.Services;
using Auricrux.Web.Services.Breakthrough;
using Auricrux.Web.Services.PhaseII;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddControllers();
builder.Services.AddHttpClient(nameof(ConstructionIntelligenceService));
builder.Services.AddHttpClient(nameof(MediaGenerationService));
builder.Services.AddHttpClient(nameof(FcaAccountLinkService));
builder.Services.AddHttpClient(nameof(BackendHealthService));
builder.Services.AddHttpClient(nameof(WebBrowseService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AuricruxWebBrowse/1.0 (+construction)");
});
// Phase 9A: FCA Ecosystem API integration
builder.Services.AddHttpClient("FcaEcosystem", client =>
{
    var baseUrl = builder.Configuration["FcaEcosystem:ApiBaseUrl"] ?? "https://futurecontractorsofamerica.com/api";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "Auricrux/1.0");
});
// Atlas services — registered before any service that depends on them
builder.Services.AddSingleton<AtlasService>();
builder.Services.AddSingleton<AtlasCorpusService>();
builder.Services.AddSingleton<AuricruxModelRouter>();

builder.Services.AddSingleton<ConstructionIntelligenceService>();
builder.Services.AddSingleton<KnowledgeGapAnalysisService>();
builder.Services.AddSingleton<CorpusImprovementService>();
builder.Services.AddSingleton<ImprovementEvaluationService>();
builder.Services.AddSingleton<ConstructionEventService>();
builder.Services.AddSingleton<ContextAwareGuidanceService>();
builder.Services.AddSingleton<LearningRecommendationService>();
builder.Services.AddSingleton<ContinuousImprovementService>();
builder.Services.AddSingleton<AuditTrailService>();
builder.Services.AddSingleton<ProvenanceService>();
builder.Services.AddSingleton<MediaGenerationService>();

// Phase 9A: FCA Ecosystem integration + Predictive Intelligence (Breakthrough)
builder.Services.AddSingleton<FcaEcosystemApiService>();
builder.Services.AddSingleton<BreakthroughLoopStore>();
builder.Services.AddSingleton<PredictiveIntelligenceService>();
builder.Services.AddSingleton<AcademyLessonMatcherService>();

// Phase 9B: Observability Dashboard
builder.Services.AddSingleton<IntelligenceDashboardService>();

// Phase 4 Breakthrough: self-correction loop (hypotheses → verify → meta-learn → proof)
builder.Services.AddSingleton<HypothesisEngine>();
builder.Services.AddSingleton<PhysicalVerificationService>();
builder.Services.AddSingleton<MetaLearningService>();
builder.Services.AddSingleton<ProvableReasoningService>();
builder.Services.AddSingleton<FoundationPourDemoService>();
builder.Services.AddSingleton<PedagogyActService>();

// Background services
builder.Services.AddHostedService<Auricrux.Web.BackgroundServices.LearningPipelineWorker>();
builder.Services.AddHostedService<Auricrux.Web.BackgroundServices.PredictiveIntelligenceOrchestrator>();
builder.Services.AddSingleton<WorkspaceStorageService>();
builder.Services.AddSingleton<ConversationMemoryService>();
builder.Services.AddSingleton<FcaAccountLinkService>();
builder.Services.AddSingleton<FreemiumAccountStore>();
builder.Services.AddSingleton<BackendHealthService>();
builder.Services.AddSingleton<WebBrowseService>();
builder.Services.AddSingleton<ConstructionCalculatorService>();
builder.Services.AddSingleton<ConstructionAgentService>();
builder.Services.AddSingleton<ConstructionVisionService>();
builder.Services.AddSingleton<CapabilitiesService>();
builder.Services.AddSingleton<PackageIdentityService>();
builder.Services.AddSingleton<RuntimeTruthService>();
builder.Services.AddSingleton(sp =>
{
    var configured = builder.Configuration["Auricrux:ApiEndpoint"]
        ?? builder.Configuration["Auricrux:PublicBaseUrl"]
        ?? "http://localhost:5080/";
    return new AuricruxConfig
    {
        ApiEndpoint = configured.TrimEnd('/') + "/",
        EnableAutoSpeak = false,
        EnableLogging = true,
        TimeoutSeconds = 180
    };
});
builder.Services.AddHttpClient<AuricruxApiClient>((sp, client) =>
{
    var cfg = sp.GetRequiredService<AuricruxConfig>();
    client.BaseAddress = new Uri(cfg.ApiEndpoint);
});
builder.Services.AddSingleton<TextToSpeechService>();
builder.Services.AddScoped<AuricruxService>();

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5080", "https://localhost:7080", "https://auricrux.futurecontractorsofamerica.com"];
builder.Services.AddCors(options =>
{
    options.AddPolicy("auricrux", policy =>
        policy.WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", httpContext =>
    {
        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimit:PermitLimit", 120),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

// Default-deny by design: Auth:Enabled must be explicitly true AND an Authority configured
// before any authentication/authorization middleware is registered. When enabled, API clients
// (mobile/curl) authenticate with a JWT bearer token; browsers get an interactive OIDC login
// (Authorization Code flow) backed by a cookie session, wired via Auth:ClientId below.
var authEnabled = builder.Configuration.GetValue("Auth:Enabled", false);
var authAuthority = builder.Configuration["Auth:Authority"];
var oidcClientId = builder.Configuration["Auth:ClientId"];
var oidcConfigured = authEnabled && !string.IsNullOrWhiteSpace(authAuthority) && !string.IsNullOrWhiteSpace(oidcClientId);
if (authEnabled && !string.IsNullOrWhiteSpace(authAuthority))
{
    var authBuilder = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.Authority = authAuthority.TrimEnd('/');
            options.Audience = builder.Configuration["Auth:Audience"] ?? "auricrux";
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = !string.IsNullOrWhiteSpace(builder.Configuration["Auth:Audience"]),
                ValidateIssuer = true
            };
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
        });

    if (oidcConfigured)
    {
        authBuilder.AddOpenIdConnect("oidc", options =>
        {
            options.Authority = authAuthority.TrimEnd('/');
            options.ClientId = oidcClientId;
            options.ClientSecret = builder.Configuration["Auth:ClientSecret"];
            options.ResponseType = "code";
            options.SaveTokens = true;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
        });
    }

    builder.Services.AddAuthorization();
}

var app = builder.Build();

var mediaRoot = Path.Combine(app.Environment.ContentRootPath, "Data", "media");
Directory.CreateDirectory(mediaRoot);
var workspaceRoot = Path.Combine(app.Environment.ContentRootPath, "Data", "workspace");
Directory.CreateDirectory(workspaceRoot);

// Ensure Atlas indexes for learning pipeline collections
var atlas = app.Services.GetRequiredService<AtlasService>();
_ = Task.Run(async () => await atlas.EnsureIndexesAsync());
var loopStore = app.Services.GetRequiredService<BreakthroughLoopStore>();
try
{
    using var hydrateCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    loopStore.HydrateFromAtlasAsync(hydrateCts.Token).GetAwaiter().GetResult();
}
catch
{
    // Live still serves from process memory if Atlas hydrate times out.
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCorrelationId();
app.UseApiExceptionHandling();
app.UseAuricruxApiMiddleware();
app.UseCors("auricrux");
app.UseRateLimiter();
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(mediaRoot),
    RequestPath = "/media"
});
app.UseAntiforgery();

if (authEnabled && !string.IsNullOrWhiteSpace(authAuthority))
{
    app.UseAuthentication();
    app.UseAuthorization();

    if (oidcConfigured)
    {
        app.MapGet("/account/login", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl },
                ["oidc"]));

        app.MapPost("/account/logout", (HttpContext ctx) =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, "oidc"]));
    }
}

// Reads Auth:* live from IConfiguration (not the builder-time-captured locals above) so this
// endpoint — and integration tests that layer configuration on after the host is built — always
// see the actual effective configuration.
app.MapGet("/account/auth-status", (IConfiguration liveConfig) =>
{
    var liveAuthEnabled = liveConfig.GetValue("Auth:Enabled", false);
    var liveAuthority = liveConfig["Auth:Authority"];
    var liveClientId = liveConfig["Auth:ClientId"];
    var liveOidcConfigured = liveAuthEnabled
        && !string.IsNullOrWhiteSpace(liveAuthority)
        && !string.IsNullOrWhiteSpace(liveClientId);

    return Results.Ok(new
    {
        authEnabled = liveAuthEnabled,
        oidcConfigured = liveOidcConfigured
    });
});

app.MapStaticAssets();
app.MapControllers().RequireRateLimiting("api");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
