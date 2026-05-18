namespace DbExplorer.Options;

/// <summary>
/// Feature-flag configuration for authentication providers.
/// The local credential store is on by default; external providers are disabled by default.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Built-in username/password (PBKDF2) credential store.</summary>
    public LocalAuthOptions Local { get; init; } = new();

    /// <summary>Windows Negotiate (Kerberos/NTLM) authentication.</summary>
    public WindowsAuthOptions Windows { get; init; } = new();

    /// <summary>Google OAuth 2.0 authentication.</summary>
    public GoogleAuthOptions Google { get; init; } = new();

    /// <summary>
    /// Returns true when the local login form should be shown and the /api/login endpoint
    /// should accept requests. The local store is active when:
    ///   - Auth:Local:Enabled is explicitly true, OR
    ///   - No external providers are enabled (automatic fallback — prevents being locked out).
    /// </summary>
    public bool IsLocalLoginActive =>
        Local.Enabled || (!Windows.Enabled && !Google.Enabled);
}

public sealed class LocalAuthOptions
{
    /// <summary>
    /// Enable the built-in username/password login form and /api/login endpoint.
    /// Defaults to <c>true</c>. Set to <c>false</c> when all users should authenticate via
    /// an external provider (Windows or Google). The app will automatically keep the local
    /// store active if no external provider is enabled, preventing accidental lockout.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

public sealed class WindowsAuthOptions
{
    /// <summary>Enable Windows Negotiate authentication. Requires IIS or Kestrel running on a domain machine.</summary>
    public bool Enabled { get; init; } = false;
}

public sealed class GoogleAuthOptions
{
    /// <summary>Enable Google OAuth 2.0 sign-in.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Google OAuth client ID from Google Cloud Console.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Google OAuth client secret. Keep this in user secrets or environment variables —
    /// never commit it to source control.
    /// </summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>
    /// Optional email allow-list. Each entry is a glob pattern matched against the
    /// authenticated Google account's email address. The <c>*</c> wildcard matches any
    /// sequence of characters, including dots and hyphens.
    ///
    /// Examples:
    ///   - Exact match:          "alice@example.com"
    ///   - Any @example.com:     "*@example.com"
    ///   - Any sub-domain:       "*@*.example.com"  — also matches deeper levels like x.y.example.com
    ///   - Any Google account:   leave the list empty (default)
    ///
    /// Note: <c>*</c> in the domain part is greedy — <c>*@*.example.com</c> matches
    /// <c>user@a.example.com</c> AND <c>user@a.b.example.com</c>. If you need a single-level
    /// sub-domain match only, enumerate each allowed sub-domain explicitly.
    ///
    /// When the list is empty, any authenticated Google account is allowed.
    /// </summary>
    public List<string> AllowList { get; init; } = [];
}
