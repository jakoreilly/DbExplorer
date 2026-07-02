namespace DbExplorer.Services;

/// <summary>
/// Resolves the current target server and optional per-request database.
/// </summary>
public interface IRequestServerContext
{
    string CurrentServer { get; }
    string? CurrentDatabase { get; }
}
