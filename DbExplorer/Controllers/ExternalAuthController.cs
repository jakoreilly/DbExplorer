using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace DbExplorer.Controllers;

/// <summary>
/// Handles external authentication flows (Windows Negotiate and Google OAuth).
/// </summary>
[AllowAnonymous]
[ApiController]
public sealed class ExternalAuthController(
    IOptions<AuthOptions> authOptions,
    IAuditLogger audit,
    ILogger<ExternalAuthController> logger) : ControllerBase
{
    // ── Windows Auth ──────────────────────────────────────────────────────────

    /// <summary>
    /// Triggers Windows Negotiate authentication and issues a cookie session on success.
    /// We manually challenge rather than using [Authorize(Scheme=Negotiate)] so the feature-flag
    /// check runs first and returns 503 before the Negotiate handshake is even attempted.
    /// </summary>
    [HttpGet("/auth/windows")]
    public async Task<IActionResult> WindowsSignIn([FromQuery] string returnUrl = "/")
    {
        var opts = authOptions.Value;
        if (!opts.Windows.Enabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Windows authentication is not enabled.");

        // Manually authenticate with the Negotiate scheme so the feature-flag check above runs first.
        var result = await HttpContext.AuthenticateAsync(NegotiateDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            // Trigger the 401 Negotiate challenge so the browser sends NTLM/Kerberos credentials.
            await HttpContext.ChallengeAsync(NegotiateDefaults.AuthenticationScheme);
            return new EmptyResult(); // response already written by ChallengeAsync
        }

        var username = result.Principal.Identity?.Name ?? "anonymous";

        // Issue a cookie so subsequent requests are authenticated without re-negotiating.
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, "Explorer"),
            new Claim("auth_provider", "windows"),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false });

        logger.LogInformation("Windows user '{Username}' signed in via Negotiate", username);
        audit.Log(new AuditEvent(DateTimeOffset.UtcNow, username, AuditAction.Login,
            null, null, -1, -1, Provider: "windows"));

        var safeReturn = IsLocalUrl(returnUrl) ? returnUrl : "/";
        return Redirect(safeReturn);
    }

    // ── Google OAuth ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initiates the Google OAuth 2.0 authorisation code flow.
    /// </summary>
    [HttpGet("/auth/google")]
    public IActionResult GoogleSignIn([FromQuery] string returnUrl = "/")
    {
        var opts = authOptions.Value;
        if (!opts.Google.Enabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Google authentication is not enabled.");

        var safeReturn = IsLocalUrl(returnUrl) ? returnUrl : "/";
        var properties = new AuthenticationProperties { RedirectUri = $"/auth/google/callback?returnUrl={Uri.EscapeDataString(safeReturn)}" };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// OAuth 2.0 callback. Google redirects here after the user approves the consent screen.
    /// Validates the email against the configured allow-list, then issues a cookie session.
    /// </summary>
    [HttpGet("/auth/google/callback")]
    public async Task<IActionResult> GoogleCallback([FromQuery] string returnUrl = "/")
    {
        var opts = authOptions.Value;
        if (!opts.Google.Enabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Google authentication is not enabled.");

        // Exchange the code for tokens and retrieve the Google identity.
        var result = await HttpContext.AuthenticateAsync(GoogleDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            logger.LogWarning("Google OAuth callback failed: {Error}", result.Failure?.Message);
            return Redirect("/login?error=google");
        }

        var email = result.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning("Google OAuth callback: no email claim in identity");
            return Redirect("/login?error=google");
        }

        // Check allow-list. An empty list allows all authenticated Google accounts.
        if (opts.Google.AllowList.Count > 0 && !EmailMatchesAllowList(email, opts.Google.AllowList))
        {
            logger.LogWarning("Google sign-in rejected: email '{Email}' not in allow-list", email);
            audit.Log(new AuditEvent(DateTimeOffset.UtcNow, email, AuditAction.LoginFailed,
                null, null, -1, -1, Provider: "google"));
            return Redirect("/login?error=denied");
        }

        var name = result.Principal.FindFirstValue(ClaimTypes.Name) ?? email;
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, email),
            new Claim("display_name", name),
            new Claim(ClaimTypes.Role, "Explorer"),
            new Claim("auth_provider", "google"),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false });

        logger.LogInformation("Google user '{Email}' signed in", email);
        audit.Log(new AuditEvent(DateTimeOffset.UtcNow, email, AuditAction.Login,
            null, null, -1, -1, Provider: "google"));

        var safeReturn = IsLocalUrl(returnUrl) ? returnUrl : "/";
        return Redirect(safeReturn);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches an email address against a list of glob patterns.
    /// Supported patterns:
    ///   - Exact match:   "alice@example.com"
    ///   - Domain:        "*@example.com"
    ///   - Sub-domain:    "*@*.example.com"
    ///   - Allow all:     "*@*.*"
    /// </summary>
    internal static bool EmailMatchesAllowList(string email, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (GlobMatches(pattern, email))
                return true;
        }
        return false;
    }

    private static bool GlobMatches(string pattern, string input)
    {
        // Split on @ to handle local-part and domain separately
        var patternParts = pattern.Split('@');
        var inputParts = input.Split('@');
        if (patternParts.Length != 2 || inputParts.Length != 2)
            return false;

        return WildcardMatch(patternParts[0], inputParts[0], caseSensitive: false)
            && WildcardMatch(patternParts[1], inputParts[1], caseSensitive: false);
    }

    /// <summary>Simple wildcard match where * matches any sequence of characters.</summary>
    private static bool WildcardMatch(string pattern, string input, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // DP-based wildcard matching
        var p = pattern.AsSpan();
        var s = input.AsSpan();
        int pi = 0, si = 0, starPi = -1, starSi = -1;

        while (si < s.Length)
        {
            if (pi < p.Length && (p[pi] == '*'))
            {
                starPi = pi++;
                starSi = si;
            }
            else if (pi < p.Length && CharEqual(p[pi], s[si], comparison))
            {
                pi++;
                si++;
            }
            else if (starPi >= 0)
            {
                pi = starPi + 1;
                si = ++starSi;
            }
            else
            {
                return false;
            }
        }

        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    private static bool CharEqual(char a, char b, StringComparison comparison) =>
        comparison == StringComparison.OrdinalIgnoreCase
            ? char.ToUpperInvariant(a) == char.ToUpperInvariant(b)
            : a == b;

    /// <summary>Prevents open redirect attacks by allowing only local (same-origin) URLs.</summary>
    private static bool IsLocalUrl(string url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
}
