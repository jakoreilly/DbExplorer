using Microsoft.Data.SqlClient;

namespace DbExplorer.Services;

/// <summary>
/// Creates SqlConnection instances from the configured connection string.
/// The connection string must not include any user-supplied values.
/// </summary>
public sealed class SqlConnectionFactory(string connectionString)
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    public SqlConnection Create() => new(_connectionString);
}
