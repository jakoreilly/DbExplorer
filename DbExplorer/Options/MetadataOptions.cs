namespace DbExplorer.Options;

/// <summary>
/// Tuning for the metadata layer (object tree, columns, search).
/// </summary>
public sealed class MetadataOptions
{
    public const string SectionName = "Metadata";

    /// <summary>
    /// How long (seconds) hot metadata lookups (schemas/objects/columns/search)
    /// are cached per connection+catalog. Set to 0 to disable caching.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 60;
}
