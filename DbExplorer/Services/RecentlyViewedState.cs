namespace DbExplorer.Services;

/// <summary>
/// Scoped service that broadcasts a notification whenever a new object is added
/// to the recently-viewed list, so the <c>RecentlyViewed</c> sidebar component
/// can reload its list without polling.
/// </summary>
public sealed class RecentlyViewedState
{
    /// <summary>Raised on the circuit's synchronisation context after a new item is recorded.</summary>
    public event Action? OnItemAdded;

    /// <summary>
    /// Called by <c>ExplorerPage</c> after it has written the new entry to localStorage
    /// via <c>dbExplorerAddRecent</c>.
    /// </summary>
    public void NotifyItemAdded() => OnItemAdded?.Invoke();
}
