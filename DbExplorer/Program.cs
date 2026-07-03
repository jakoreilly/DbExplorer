using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using DbExplorer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Serilog;
using System.Threading.RateLimiting;
var builder = WebApplication.CreateBuilder(args);

// Load local developer secrets from the machine-specific user-secrets store.
// These values are read from the OS profile and are not part of the repository.
builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: true);

// ── Serilog ──────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/dbexplorer-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ProblemDetails
builder.Services.AddProblemDetails();

// Authentication — cookie is the primary (session) scheme; external providers are feature-flagged.
builder.Services.AddOptions<AuthOptions>().Bind(builder.Configuration.GetSection("Auth"));
var authOpts = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();

var authBuilder = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

if (authOpts.Windows.Enabled)
{
    authBuilder.AddNegotiate();
}

if (authOpts.Google.Enabled)
{
    if (string.IsNullOrWhiteSpace(authOpts.Google.ClientId) || string.IsNullOrWhiteSpace(authOpts.Google.ClientSecret))
    {
        // Log via Serilog so the warning reaches all configured sinks (file, console, etc.),
        // not just the process stderr that Console.Error writes to.
        Log.Warning(
            "Auth:Google:Enabled is true but Auth:Google:ClientId or Auth:Google:ClientSecret is not configured. " +
            "Google sign-in will be unavailable until both values are set.");
    }

    authBuilder.AddGoogle(options =>
    {
        options.ClientId = authOpts.Google.ClientId;
        options.ClientSecret = authOpts.Google.ClientSecret;
        options.CallbackPath = "/signin-google";
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    });
}

builder.Services.AddAuthorization();

// Rate limiting — fixed window per IP
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("api", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 120;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 10;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Application services
builder.Services.AddScoped<ThemeState>();
builder.Services.AddScoped<DensityState>();
builder.Services.AddScoped<RecentlyViewedState>();
builder.Services.AddScoped<PinnedState>();
builder.Services.AddScoped<DatabaseSelectorState>();
builder.Services.AddScoped<DiagramInteropService>();
builder.Services.AddOptions<DataBrowsingOptions>()
    .Bind(builder.Configuration.GetSection("DataBrowsing"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<ProfilerOptions>()
    .Bind(builder.Configuration.GetSection("Profiler"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<QueryBuilderOptions>()
    .Bind(builder.Configuration.GetSection("QueryBuilder"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<McpOptions>()
    .Bind(builder.Configuration.GetSection("Mcp"));
builder.Services.AddOptions<MetadataOptions>()
    .Bind(builder.Configuration.GetSection(MetadataOptions.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<MetadataCacheVersion>();
builder.Services.AddScoped<IQueryBuilderService, QueryBuilderService>();

// MCP server — only registered when enabled
var mcpOpts = builder.Configuration.GetSection("Mcp").Get<McpOptions>() ?? new McpOptions();
if (mcpOpts.Enabled)
{
    if (string.IsNullOrWhiteSpace(mcpOpts.ApiKey))
    {
        // Log at startup so the problem is visible immediately in all configured sinks.
        Log.Warning(
            "Mcp:Enabled is true but Mcp:ApiKey is not configured. " +
            "All MCP requests will be rejected with HTTP 503 until a strong ApiKey is set.");
    }

    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithTools<DbExplorerMcpTools>();
}

builder.Services.AddScoped<IRequestServerContext, RequestServerContext>();
builder.Services.AddScoped<IDbConnectionFactory>(sp =>
    new DbConnectionFactory(
        sp.GetRequiredService<DatabaseSelectorState>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<IRequestServerContext>()));
builder.Services.AddScoped(sp =>
    new SqlDialect(sp.GetRequiredService<IDbConnectionFactory>()));
builder.Services.AddScoped<IIdentifierValidator, IdentifierValidatorService>();
builder.Services.AddScoped<IMetadataService, MetadataService>();
builder.Services.AddScoped<IDataBrowsingService, DataBrowsingService>();
builder.Services.AddScoped<IQueryProfiler, QueryProfilerService>();
builder.Services.AddScoped<IAdHocQueryService, AdHocQueryService>();
builder.Services.AddScoped<IPersistentQueryHistoryService, PersistentQueryHistoryService>();

// Audit logging
builder.Services.AddOptions<AuditOptions>().Bind(builder.Configuration.GetSection("Audit"));
builder.Services.AddSingleton<IAuditLogger, AuditLoggerService>();

// IHttpContextAccessor is used by DbExplorerMcpTools and request-scoped server selection.
builder.Services.AddHttpContextAccessor();

// ── App ───────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseSerilogRequestLogging();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// MCP Bearer token guard — must run before endpoint routing executes the handler
if (mcpOpts.Enabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/mcp"))
        {
            var opts = context.RequestServices.GetRequiredService<IOptions<McpOptions>>().Value;

            // Refuse to serve MCP requests when no API key is configured — prevents
            // accidental exposure of database access after enabling Mcp:Enabled without
            // setting a strong Mcp:ApiKey.
            if (string.IsNullOrWhiteSpace(opts.ApiKey))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync(
                    "MCP is enabled but Mcp:ApiKey is not configured. " +
                    "Set a strong random secret in appsettings.json or environment variables.");
                return;
            }

            var authHeader = context.Request.Headers.Authorization.ToString();
            var expected = $"Bearer {opts.ApiKey}";
            if (!string.Equals(authHeader, expected, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }
        await next();
    });
}

app.MapControllers().RequireAuthorization().RequireRateLimiting("api");
app.MapRazorComponents<DbExplorer.Components.App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

// Auth pages don't require login
// GET /login is handled by the Blazor Login.razor page (with <AntiforgeryToken />)
app.MapPost("/api/login", LoginHandler.Handle).AllowAnonymous();
// Logout is POST-only to prevent CSRF-based forced logout via GET requests (e.g. <img> tags).
app.MapPost("/logout", (Delegate)LogoutHandler.Handle).AllowAnonymous();

// MCP endpoint — protected by Bearer token, only mapped when enabled
if (mcpOpts.Enabled)
{
    app.MapMcp("/mcp").AllowAnonymous()  // AllowAnonymous because auth is done by the middleware above
        .RequireRateLimiting("api");     // share the same rate-limit policy as API routes
}

app.Run();


