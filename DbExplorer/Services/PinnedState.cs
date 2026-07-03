namespace DbExplorer.Services;

/// <summary>
/// Scoped service that broadcasts a notification whenever the set of pinned
/// objects changes (pin or unpin), so the <c>PinnedObjects</c> sidebar component
/// and the pin button on <c>ExplorerPage</c> stay in sync without polling.
/// </summary>
public sealed class PinnedState
{
    /// <summary>Raised on the circuit's synchronisation context after a pin is toggled.</summary>
    public event Action? OnChange;

    /// <summary>
    /// Called after the pinned list in localStorage has been updated
    /// via <c>dbExplorerTogglePinned</c>.
    /// </summary>
    public void NotifyChanged() => OnChange?.Invoke();
}
