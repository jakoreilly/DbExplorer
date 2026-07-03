namespace DbExplorer.Services;

/// <summary>
/// Scoped service holding the user's table density preference
/// (comfortable vs. compact). Persisted in localStorage by the
/// <c>DensityToggle</c> component; the <c>MainLayout</c> applies a
/// <c>density-compact</c> class so every data table tightens up.
/// </summary>
public sealed class DensityState
{
    public bool IsCompact { get; private set; }

    public event Action? OnChange;

    public void SetCompact(bool compact)
    {
        if (IsCompact == compact) return;
        IsCompact = compact;
        OnChange?.Invoke();
    }

    public void Toggle() => SetCompact(!IsCompact);
}
