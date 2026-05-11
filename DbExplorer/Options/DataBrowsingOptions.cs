using System.ComponentModel.DataAnnotations;

namespace DbExplorer.Options;

public sealed class DataBrowsingOptions
{
    [Range(1, 10_000)]
    public int MaxPageSize { get; init; } = 500;

    [Range(1, 500)]
    public int DefaultPageSize { get; init; } = 50;

    [Range(1, 600)]
    public int QueryTimeoutSeconds { get; init; } = 30;

    [Range(1, 1_000_000)]
    public int MaxExportRows { get; init; } = 100_000;

    /// <summary>
    /// When false, skips the COUNT(*) query before fetching each page.
    /// Improves performance on very large tables/views; total row count shows as unknown in the UI.
    /// </summary>
    public bool EagerRowCount { get; init; } = true;
}
