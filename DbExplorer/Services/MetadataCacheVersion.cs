using System.Collections.Concurrent;

namespace DbExplorer.Services;

/// <summary>
/// Singleton per-connection cache version counter. Metadata cache keys embed the
/// current version, so bumping it invalidates every cached lookup for that
/// connection without having to enumerate <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> keys.
/// </summary>
public sealed class MetadataCacheVersion
{
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current version for a named connection (0 if never bumped).</summary>
    public long Get(string connectionName) => _versions.GetValueOrDefault(connectionName);

    /// <summary>Invalidates all cached metadata for a named connection.</summary>
    public void Bump(string connectionName) =>
        _versions.AddOrUpdate(connectionName, 1, (_, v) => v + 1);
}
