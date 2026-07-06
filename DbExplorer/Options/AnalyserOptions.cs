using System.ComponentModel.DataAnnotations;

namespace DbExplorer.Options;

/// <summary>
/// Controls the Systems Analyser dashboard. Configure under the "Analyser"
/// key in appsettings.json.
/// </summary>
public sealed class AnalyserOptions
{
    /// <summary>When false, event recording is a no-op and /analyser shows a disabled notice.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum events kept in the rolling in-memory buffer.</summary>
    [Range(100, 100_000)]
    public int BufferSize { get; init; } = 5000;
}
