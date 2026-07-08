namespace DbExplorer.Services;

/// <summary>
/// Scoped (per-circuit) carrier for a query handed from the Search page to the Profiler.
/// The Profiler consumes it once on load, then clears it.
/// </summary>
public sealed class QueryHandoffState
{
    public string? PendingSql { get; private set; }

    public void Set(string sql) => PendingSql = sql;

    public string? Consume()
    {
        var sql = PendingSql;
        PendingSql = null;
        return sql;
    }
}
