using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using Microsoft.Extensions.Options;

namespace DbExplorer.Services;

/// <summary>
/// Singleton, thread-safe rolling buffer of <see cref="DbActionEvent"/>s.
/// Unlike the per-circuit <see cref="QueryProfilerService"/>, this instance is
/// shared by every circuit and API request, so all buffer access is locked.
/// </summary>
public sealed class SystemAnalyserStore : ISystemAnalyserStore
{
    private readonly object _gate = new();
    private readonly Queue<DbActionEvent> _events = new();
    private readonly bool _enabled;
    private readonly int _bufferSize;
    private readonly TimeSpan _maxAge;
    private readonly ILogger<SystemAnalyserStore> _logger;

    public event Action? OnEvent;

    public SystemAnalyserStore(
        IOptions<AnalyserOptions> options,
        ILogger<SystemAnalyserStore> logger)
    {
        _enabled = options.Value.Enabled;
        _bufferSize = options.Value.BufferSize;
        _maxAge = TimeSpan.FromMinutes(options.Value.MaxAgeMinutes);
        _logger = logger;
    }

    public void Record(DbActionEvent evt)
    {
        if (!_enabled) return;
        try
        {
            lock (_gate)
            {
                while (_events.Count >= _bufferSize)
                    _events.Dequeue();

                // Events enqueue in timestamp order, so the oldest is always at the front.
                var cutoff = DateTimeOffset.UtcNow - _maxAge;
                while (_events.Count > 0 && _events.Peek().Timestamp < cutoff)
                    _events.Dequeue();

                _events.Enqueue(evt);
            }
            // Invoke OUTSIDE the lock: a subscriber that re-enters GetEvents must not deadlock.
            OnEvent?.Invoke();
        }
        catch (Exception ex)
        {
            // Telemetry must never break the action it observes.
            _logger.LogWarning(ex, "SystemAnalyserStore.Record failed for {Operation}", evt.Operation);
        }
    }

    public void RecordError(
        string provider,
        DbActionCategory category,
        string operation,
        Exception ex,
        string? schemaName = null,
        string? objectName = null,
        long elapsedMs = -1,
        string username = "anonymous",
        string? sql = null)
        => Record(new DbActionEvent(
            DateTimeOffset.UtcNow, provider, category, operation,
            schemaName, objectName, elapsedMs, RowCount: -1,
            Success: false,
            ErrorType: ex.GetType().Name,
            ErrorMessage: ex.Message,
            Username: username,
            Sql: sql));

    public IReadOnlyList<DbActionEvent> GetEvents(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        lock (_gate)
        {
            return _events.Where(e => e.Timestamp >= cutoff)
                          .Reverse()
                          .ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }
}
