using Microsoft.AspNetCore.Http;

namespace DbExplorer.Services;

/// <summary>
/// Scoped resolver that prefers a per-HTTP-request override (set by
/// <see cref="Middleware.RequestServerMiddleware"/>) and falls back to the
/// circuit-scoped <see cref="DatabaseSelectorState"/>.
/// </summary>
public sealed class RequestServerContext(
    IHttpContextAccessor httpContextAccessor,
    DatabaseSelectorState selectorState) : IRequestServerContext
{
    internal const string ServerItemKey = "DbExplorer.RequestServer";
    internal const string DatabaseItemKey = "DbExplorer.RequestDatabase";

    public string CurrentServer
    {
        get
        {
            var items = httpContextAccessor.HttpContext?.Items;
            if (items is not null &&
                items.TryGetValue(ServerItemKey, out var value) &&
                value is string server &&
                !string.IsNullOrWhiteSpace(server))
            {
                return server;
            }

            return selectorState.Current.Name;
        }
    }

    public string? CurrentDatabase
    {
        get
        {
            var items = httpContextAccessor.HttpContext?.Items;
            if (items is not null &&
                items.TryGetValue(DatabaseItemKey, out var value) &&
                value is string database &&
                !string.IsNullOrWhiteSpace(database))
            {
                return database;
            }

            return items is not null && items.ContainsKey(ServerItemKey)
                ? null
                : selectorState.SelectedCatalog;
        }
    }
}
