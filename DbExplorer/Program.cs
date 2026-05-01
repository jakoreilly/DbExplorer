using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

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

// Authentication — cookie auth; replace with OIDC or Windows Auth as needed
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/access-denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

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
builder.Services.AddScoped<DatabaseSelectorState>();
builder.Services.AddScoped<IDbConnectionFactory>(sp =>
    new DbConnectionFactory(
        sp.GetRequiredService<DatabaseSelectorState>(),
        sp.GetRequiredService<IConfiguration>()));
builder.Services.AddScoped(sp =>
    new SqlDialect(sp.GetRequiredService<IDbConnectionFactory>()));
builder.Services.AddScoped<IIdentifierValidator, IdentifierValidatorService>();
builder.Services.AddScoped<IMetadataService, MetadataService>();
builder.Services.AddScoped<IDataBrowsingService, DataBrowsingService>();

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

app.MapControllers().RequireAuthorization().RequireRateLimiting("api");
app.MapRazorComponents<DbExplorer.Components.App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

// Auth pages don't require login
// GET /login is handled by the Blazor Login.razor page (with <AntiforgeryToken />)
app.MapPost("/api/login", LoginHandler.Handle).AllowAnonymous();
app.MapPost("/logout", LogoutHandler.Handle);

// Generate a PBKDF2 hash for a password:
//var hash = BCryptHelper.Hash("YourPassword");
//Console.WriteLine(hash);
app.Run();


