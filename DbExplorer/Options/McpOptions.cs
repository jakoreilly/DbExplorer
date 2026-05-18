namespace DbExplorer.Options;

/// <summary>
/// Controls the Model Context Protocol (MCP) server endpoint.
/// Configure under the "Mcp" key in appsettings.json.
/// </summary>
public sealed class McpOptions
{
    /// <summary>
    /// When <c>true</c>, the MCP endpoint is registered at <c>/mcp</c>.
    /// Set to <c>false</c> to disable entirely.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Required bearer token for all MCP requests.
    /// All requests must include <c>Authorization: Bearer &lt;ApiKey&gt;</c>.
    /// Must be set to a strong random secret before enabling.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;
}
