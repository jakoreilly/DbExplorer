namespace DbExplorer.Services;

public sealed class ThemeState
{
    public event Action? OnChange;

    public bool IsDarkMode { get; private set; }

    public void Toggle()
    {
        IsDarkMode = !IsDarkMode;
        OnChange?.Invoke();
    }
}
