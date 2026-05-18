namespace DbExplorer.Options;

/// <summary>
/// Feature-flag configuration for authentication providers.
/// All external providers are disabled by default.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Windows Negotiate (Kerberos/NTLM) authentication.</summary>
    public WindowsAuthOptions Windows { get; init; } = new();

    /// <summary>Google OAuth 2.0 authentication.</summary>
    public GoogleAuthOptions Google { get; init; } = new();
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
    /// authenticated Google account's email address:
    ///   - Exact match:   "alice@example.com"
    ///   - Domain:        "*@example.com"
    ///   - Sub-domain:    "*@*.example.com"
    ///   - Allow all:     "*@*.*"  (default when the list is empty)
    ///
    /// When the list is empty, any authenticated Google account is allowed.
    /// </summary>
    public List<string> AllowList { get; init; } = [];
}
