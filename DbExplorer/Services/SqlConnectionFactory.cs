using MySqlConnector;
using Npgsql;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace DbExplorer.Services;

/// <summary>
/// Supported database providers.
/// </summary>
public enum DatabaseProvider
{
    SqlServer,
    PostgreSql,
    MySql
}

public interface IDbConnectionFactory
{
    DatabaseProvider Provider { get; }
    DbConnection Create();
}

public sealed record DatabaseConnectionOption(
    string Name,
    DatabaseProvider Provider,
    string ConnectionStringName);

/// <summary>
/// Holds the active database target selected by the user.
/// Targets are loaded from configuration and validated against known providers.
/// </summary>
public sealed class DatabaseSelectorState
{
    private readonly object _sync = new();
    private string _selectedName;
    private string? _selectedCatalog;

    public event Action? OnChange;

    public DatabaseSelectorState(IConfiguration configuration)
    {
        var configuredOptions = LoadConfiguredOptions(configuration).ToList();

        if (configuredOptions.Count == 0)
        {
            configuredOptions =
            [
                new DatabaseConnectionOption("SQL Server", DatabaseProvider.SqlServer, "SqlServer"),
                new DatabaseConnectionOption("PostgreSQL", DatabaseProvider.PostgreSql, "PostgreSql"),
                new DatabaseConnectionOption("MySQL", DatabaseProvider.MySql, "MySql")
            ];
        }

        Options = configuredOptions;

        var configuredDefault = configuration["DbExplorer:Database:Selected"];
        if (!string.IsNullOrWhiteSpace(configuredDefault) &&
            Options.Any(o => string.Equals(o.Name, configuredDefault, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedName = configuredDefault;
            return;
        }

        // Backward-compatible defaulting: honor existing DbExplorer:Database settings if present.
        var legacyProviderText = configuration["DbExplorer:Database:Provider"];
        var legacyConnectionName = configuration["DbExplorer:Database:ConnectionStringName"];
        if (Enum.TryParse<DatabaseProvider>(legacyProviderText, ignoreCase: true, out var legacyProvider))
        {
            var legacyMatch = Options.FirstOrDefault(o =>
                o.Provider == legacyProvider &&
                (string.IsNullOrWhiteSpace(legacyConnectionName) ||
                 string.Equals(o.ConnectionStringName, legacyConnectionName, StringComparison.OrdinalIgnoreCase)));

            if (legacyMatch is not null)
            {
                _selectedName = legacyMatch.Name;
                return;
            }
        }

        _selectedName = Options[0].Name;
    }

    public IReadOnlyList<DatabaseConnectionOption> Options { get; }

    public DatabaseConnectionOption Current
    {
        get
        {
            lock (_sync)
            {
                return Options.First(o => string.Equals(o.Name, _selectedName, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public bool TrySetCurrent(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        bool changed;
        lock (_sync)
        {
            var match = Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return false;

            changed = !string.Equals(_selectedName, match.Name, StringComparison.OrdinalIgnoreCase);
            _selectedName = match.Name;
            _selectedCatalog = null;
        }
        if (changed) OnChange?.Invoke();
        return true;
    }

    public string? SelectedCatalog
    {
        get
        {
            lock (_sync)
            {
                return _selectedCatalog;
            }
        }
    }

    public void SetCatalog(string? catalogName)
    {
        lock (_sync)
        {
            _selectedCatalog = string.IsNullOrWhiteSpace(catalogName)
                ? null
                : catalogName.Trim();
        }
    }

    private static IEnumerable<DatabaseConnectionOption> LoadConfiguredOptions(IConfiguration configuration)
    {
        var defaultProviderText = configuration["DbExplorer:Database:Provider"] ?? "SqlServer";
        _ = Enum.TryParse<DatabaseProvider>(defaultProviderText, ignoreCase: true, out var defaultProvider);

        var entries = configuration.GetSection("DbExplorer:Databases").GetChildren();
        foreach (var entry in entries)
        {
            var name = entry["Name"];
            var providerText = entry["Provider"];
            var connName = entry["ConnectionStringName"];

            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(connName))
            {
                continue;
            }

            var provider = defaultProvider;
            if (!string.IsNullOrWhiteSpace(providerText))
            {
                if (!Enum.TryParse<DatabaseProvider>(providerText, ignoreCase: true, out provider))
                {
                    continue;
                }
            }

            yield return new DatabaseConnectionOption(name, provider, connName);
        }
    }
}

/// <summary>
/// Creates provider-specific DbConnection instances.
/// The configured connection string must be trusted application configuration.
/// </summary>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly DatabaseProvider _staticProvider;
    private readonly string? _staticConnectionString;
    private readonly DatabaseSelectorState? _selectorState;
    private readonly IConfiguration? _configuration;
    private readonly IRequestServerContext? _requestContext;

    public DbConnectionFactory(DatabaseProvider provider, string connectionString)
    {
        _staticProvider = provider;
        _staticConnectionString = connectionString
            ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public DbConnectionFactory(DatabaseSelectorState selectorState, IConfiguration configuration)
    {
        _selectorState = selectorState ?? throw new ArgumentNullException(nameof(selectorState));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public DbConnectionFactory(
        DatabaseSelectorState selectorState,
        IConfiguration configuration,
        IRequestServerContext requestContext)
        : this(selectorState, configuration)
    {
        _requestContext = requestContext ?? throw new ArgumentNullException(nameof(requestContext));
    }

    /// <summary>
    /// Active connection option: the per-request server override when one is present
    /// (API/MCP requests), otherwise the circuit's selected connection.
    /// </summary>
    private DatabaseConnectionOption ResolveOption()
    {
        var state = _selectorState!;
        if (_requestContext is null)
            return state.Current;

        var server = _requestContext.CurrentServer;
        return state.Options.FirstOrDefault(o =>
                string.Equals(o.Name, server, StringComparison.OrdinalIgnoreCase))
            ?? state.Current;
    }

    public DatabaseProvider Provider => _selectorState is null ? _staticProvider : ResolveOption().Provider;

    public DbConnection Create()
    {
        var provider = Provider;
        string connectionString;
        string? selectedCatalog;

        if (_selectorState is null)
        {
            connectionString = _staticConnectionString!;
            selectedCatalog = null;
        }
        else
        {
            var option = ResolveOption();
            connectionString = _configuration!.GetConnectionString(option.ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{option.ConnectionStringName}' is required for '{option.Name}'.");
            selectedCatalog = _requestContext is null
                ? _selectorState.SelectedCatalog
                : _requestContext.CurrentDatabase;
        }

        return provider switch
        {
            DatabaseProvider.SqlServer => CreateSqlServerConnection(connectionString, selectedCatalog),
            DatabaseProvider.PostgreSql => CreatePostgreSqlConnection(connectionString, selectedCatalog),
            DatabaseProvider.MySql => CreateMySqlConnection(connectionString, selectedCatalog),
            _ => throw new InvalidOperationException($"Unsupported provider '{provider}'.")
        };
    }

    private static Microsoft.Data.SqlClient.SqlConnection CreateSqlServerConnection(string baseConnectionString, string? catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog))
            return new Microsoft.Data.SqlClient.SqlConnection(baseConnectionString);

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = catalog
        };
        return new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
    }

    private static NpgsqlConnection CreatePostgreSqlConnection(string baseConnectionString, string? catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog))
            return new NpgsqlConnection(baseConnectionString);

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = catalog
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    private static MySqlConnection CreateMySqlConnection(string baseConnectionString, string? catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog))
            return new MySqlConnection(baseConnectionString);

        var builder = new MySqlConnectionStringBuilder(baseConnectionString)
        {
            Database = catalog
        };
        return new MySqlConnection(builder.ConnectionString);
    }
}

/// <summary>
/// Validates and quotes identifiers using a strict, cross-database-safe subset.
/// </summary>
public sealed class SqlDialect
{
    private static readonly Regex ValidIdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly Func<DatabaseProvider> _providerAccessor;

    public SqlDialect(DatabaseProvider provider)
    {
        _providerAccessor = () => provider;
    }

    public SqlDialect(IDbConnectionFactory connectionFactory)
    {
        _providerAccessor = () => connectionFactory.Provider;
    }

    public DatabaseProvider Provider => _providerAccessor();

    public void ThrowIfInvalidIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Identifier must not be empty.", paramName);

        var maxLength = Provider switch
        {
            DatabaseProvider.SqlServer => 128,
            DatabaseProvider.PostgreSql => 63,
            DatabaseProvider.MySql => 64,
            _ => 64
        };

        if (identifier.Length > maxLength || !ValidIdentifierPattern.IsMatch(identifier))
        {
            throw new ArgumentException(
                $"'{identifier}' is not a valid identifier for provider '{Provider}'.",
                paramName);
        }
    }

    public string QuoteIdentifier(string identifier)
    {
        ThrowIfInvalidIdentifier(identifier, nameof(identifier));

        return Provider switch
        {
            DatabaseProvider.SqlServer => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]",
            DatabaseProvider.PostgreSql => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
            DatabaseProvider.MySql => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`",
            _ => throw new InvalidOperationException($"Unsupported provider '{Provider}'.")
        };
    }

    public string QuoteQualifiedName(string schemaName, string objectName)
    {
        ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        ThrowIfInvalidIdentifier(objectName, nameof(objectName));
        return $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(objectName)}";
    }
}
