using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace DbExplorer.Services;

/// <summary>
/// Simple credential-based login using hashed passwords from configuration.
/// </summary>
public static class LoginHandler
{
    public static async Task<IResult> Handle(
        HttpContext context,
        IConfiguration config,
        IOptions<AuthOptions> authOptions,
        IAuditLogger audit,
        ILogger<Program> logger)
    {
        // Reject the request when the local credential store has been explicitly disabled
        // (and at least one external provider is enabled, so the effective-active guard passes).
        if (!authOptions.Value.IsLocalLoginActive)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var form = await context.Request.ReadFormAsync();
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Results.Redirect("/login?error=1");

        // Load allowed users from config section DbExplorer:Users
        var users = config.GetSection("DbExplorer:Users").Get<List<UserConfig>>() ?? [];
        var user = users.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        if (user is null || !BCryptHelper.Verify(password, user.PasswordHash))
        {
            logger.LogWarning("Failed login attempt for user '{Username}'", username);
            audit.Log(new AuditEvent(DateTimeOffset.UtcNow, username, AuditAction.LoginFailed,
                null, null, -1, -1, Provider: "local"));
            return Results.Redirect("/login?error=true");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, "Explorer")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = false });

        logger.LogInformation("User '{Username}' signed in", user.Username);
        audit.Log(new AuditEvent(DateTimeOffset.UtcNow, user.Username, AuditAction.Login,
            null, null, -1, -1, Provider: "local"));
        return Results.Redirect("/");
    }

    public record UserConfig(string Username, string PasswordHash);
}

public static class LogoutHandler
{
    public static async Task<IResult> Handle(HttpContext context, IAuditLogger audit)
    {
        var username = context.User.Identity?.Name ?? "anonymous";
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        audit.Log(new AuditEvent(DateTimeOffset.UtcNow, username, AuditAction.Logout,
            null, null, -1, -1));
        return Results.Redirect("/login");
    }
}

/// <summary>
/// Minimal BCrypt wrapper — in production use BCrypt.Net-Next NuGet package.
/// This placeholder uses PBKDF2 + SHA256 so there is no additional NuGet dependency.
/// </summary>
public static class BCryptHelper
{
    public static bool Verify(string password, string storedHash)
    {
        // storedHash format: "pbkdf2:<salt_b64>:<hash_b64>"
        try
        {
            var parts = storedHash.Split(':');
            if (parts.Length != 3 || parts[0] != "pbkdf2") return false;
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Pbkdf2(password, salt);
            return CryptographicEquals(actual, expected);
        }
        catch { return false; }
    }

    public static string Hash(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = Pbkdf2(password, salt);
        return $"pbkdf2:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static byte[] Pbkdf2(string password, byte[] salt) =>
        System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt,
            iterations: 350_000,
            hashAlgorithm: System.Security.Cryptography.HashAlgorithmName.SHA256,
            outputLength: 32);

    private static bool CryptographicEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
