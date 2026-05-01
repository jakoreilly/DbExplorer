namespace DbExplorer.Options;

public sealed class DataBrowsingOptions
{
    public int MaxPageSize { get; init; } = 500;
    public int DefaultPageSize { get; init; } = 50;
    public int QueryTimeoutSeconds { get; init; } = 30;
    public int MaxExportRows { get; init; } = 100000;
}
