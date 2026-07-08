using Dapper;
using MySqlConnector;
using Npgsql;

namespace DbExplorer.Services;

/// <summary>
/// Helpers for enumerating and pinning catalogs (databases) on a connection, so cross-connection
/// search can reach every database on a server rather than only the connection string's default.
/// </summary>
internal static class ConnectionCatalogHelper
{
    public static async Task<IReadOnlyList<string>> GetCatalogsAsync(
        DatabaseProvider provider, string baseConnectionString, int timeoutSeconds, CancellationToken ct)
    {
        var sql = provider switch
        {
            DatabaseProvider.SqlServer =>
                "SELECT name FROM sys.databases WHERE state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1 " +
                "AND name NOT IN ('master','tempdb','model','msdb') ORDER BY name",
            DatabaseProvider.PostgreSql =>
                "SELECT datname FROM pg_database WHERE datallowconn = TRUE AND datistemplate = FALSE ORDER BY datname",
            DatabaseProvider.MySql =>
                "SELECT schema_name FROM information_schema.schemata " +
                "WHERE schema_name NOT IN ('information_schema','mysql','performance_schema','sys') ORDER BY schema_name",
            _ => throw new InvalidOperationException($"Unsupported provider '{provider}'.")
        };

        var factory = new DbConnectionFactory(provider, baseConnectionString);
        await using var conn = factory.Create();
        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(sql, commandTimeout: timeoutSeconds, cancellationToken: ct));
        return rows.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
    }

    public static string WithCatalog(DatabaseProvider provider, string baseConnectionString, string catalog) =>
        provider switch
        {
            DatabaseProvider.SqlServer =>
                new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(baseConnectionString)
                { InitialCatalog = catalog }.ConnectionString,
            DatabaseProvider.PostgreSql =>
                new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = catalog }.ConnectionString,
            DatabaseProvider.MySql =>
                new MySqlConnectionStringBuilder(baseConnectionString) { Database = catalog }.ConnectionString,
            _ => baseConnectionString
        };
}
