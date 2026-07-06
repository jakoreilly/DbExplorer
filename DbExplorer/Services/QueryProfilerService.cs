using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;

namespace DbExplorer.Services;

/// <summary>
/// Scoped per Blazor circuit. Stores a rolling buffer of the last 100 queries
/// executed by MetadataService and DataBrowsingService within this circuit.
/// Scoped lifetime means a single circuit owns this instance — no cross-thread
/// access occurs, so no locking is needed.
/// </summary>
public sealed class QueryProfilerService : IQueryProfiler
{
    private const int MaxEntries = 100;
    private readonly Queue<ProfiledQuery> _history = new();
    private readonly ISystemAnalyserStore _analyser;

    public QueryProfilerService(ISystemAnalyserStore analyser)
    {
        _analyser = analyser;
    }

    public void Record(string provider, string sql, long elapsedMs, int rowCount)
    {
        if (_history.Count >= MaxEntries)
            _history.Dequeue();

        _history.Enqueue(new ProfiledQuery(
            DateTimeOffset.UtcNow,
            provider,
            sql.Trim(),
            elapsedMs,
            rowCount));

        _analyser.Record(new DbActionEvent(
            DateTimeOffset.UtcNow, provider,
            DbActionCategory.Metadata,
            Operation: sql.Length > 120 ? sql[..120] : sql,
            SchemaName: null, ObjectName: null,
            ElapsedMs: elapsedMs, RowCount: rowCount,
            Success: true, ErrorType: null, ErrorMessage: null,
            Username: "-", Sql: null));
    }

    public IReadOnlyList<ProfiledQuery> GetHistory() => _history.Reverse().ToList();

    public void Clear() => _history.Clear();
}

