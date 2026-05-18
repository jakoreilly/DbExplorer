# DbExplorer

A production-grade, read-only multi-database explorer built on .NET 10 ASP.NET Core with a Blazor Server frontend. Supports **SQL Server**, **MySQL**, and **PostgreSQL** from a single interface.

## Features

- Dynamic schema discovery at runtime — no prior knowledge of the target database required
- Browse schemas, tables, views, stored procedures, functions, and triggers
- View columns (with types, nullability, defaults, PK membership), indexes, foreign keys, and object definitions
- Read object definitions (views, stored procedures, functions, triggers) in a source viewer
- Pageable data grid with server-side paging (max 500 rows/page, default 50), column sorting
- CSV export of the current page
- Object name search/filter in the left-hand tree with recently-viewed tracking
- **Query Builder** page — build `SELECT` queries visually:
  - *Visual Canvas*: drag-and-drop tables onto a diagram, draw column-to-column JOIN links, select columns per table; SQL is compiled automatically
  - *Form Builder*: step-by-step form for selecting a base table, JOINs, column selection, filtering, sorting, and row limits
- **Query Profiler** page — ad-hoc read-only SQL editor with EXPLAIN plan support, live server activity monitor, per-session query history, and recent query statistics (requires `pg_stat_statements` on PostgreSQL)
- Dark mode / light mode theme toggle
- Rate limited API (120 req/min per IP)
- Cookie-based authentication

---

## Security Architecture

### Read-Only by Design

The application **only ever executes** `SELECT` queries and read-only catalog queries. There are no endpoints, pages, or service methods that issue `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `DROP`, `ALTER`, `CREATE`, `EXEC`, or accept arbitrary SQL from users — except through the Profiler's ad-hoc editor, which enforces read-only validation before execution (see [EnsureReadOnly](#query-profiler-read-only-enforcement)).

### Identifier Validation

Every schema and object name passes through two layers of validation before use in SQL:

1. **Static format check** (`SqlIdentifierHelper.IsValidIdentifierFormat`) — rejects names containing spaces, SQL metacharacters, or names exceeding 128 characters.
2. **Live catalog check** (`IIdentifierValidator.ValidateObjectAsync`) — parameterized-query lookup against system catalogs to confirm the name exists. Only after both checks pass does the service quote the identifier and interpolate it into a fixed, read-only SQL template.

User-supplied values (page number, page size, search filter, column name) are **never** interpolated into SQL — they are passed as Dapper parameters.

### Query Profiler Read-Only Enforcement

The `EnsureReadOnly` guard in `AdHocQueryService` strips both block and line comments from submitted SQL, then:

1. Rejects multi-statement batches (`;` separator)
2. Requires the statement to begin with `SELECT`, `WITH` (CTE), `SHOW`, `EXPLAIN`, `DESCRIBE`, or `DESC`
3. Scans the full text for write-DML keywords (`INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE`, `EXEC`, `EXECUTE`, `MERGE`, `TRUNCATE`, `CALL`) — also blocks writable CTEs

The Profiler's SQL editor can be disabled entirely via the `Profiler:EnableQueryEditor` feature flag in `appsettings.json`.

### Least-Privilege SQL Account

The database account used by DbExplorer should be read-only. Example for each provider:

**SQL Server**
```sql
CREATE LOGIN dbexplorer_ro WITH PASSWORD = '<strong password>';
USE YourDatabase;
CREATE USER dbexplorer_ro FOR LOGIN dbexplorer_ro;
EXEC sp_addrolemember 'db_datareader', 'dbexplorer_ro';
GRANT VIEW DEFINITION TO dbexplorer_ro;
```

**MySQL**
```sql
CREATE USER 'dbexplorer_ro'@'%' IDENTIFIED BY '<strong password>';
GRANT SELECT, SHOW DATABASES, PROCESS ON *.* TO 'dbexplorer_ro'@'%';
GRANT SELECT ON your_database.* TO 'dbexplorer_ro'@'%';
FLUSH PRIVILEGES;
```

**PostgreSQL**
```sql
CREATE USER dbexplorer_ro WITH PASSWORD '<strong password>';
GRANT CONNECT ON DATABASE your_database TO dbexplorer_ro;
GRANT USAGE ON SCHEMA public TO dbexplorer_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO dbexplorer_ro;
```

### Authentication

DbExplorer uses ASP.NET Core cookie authentication. Users are configured in `appsettings.json` with PBKDF2-hashed passwords.

To generate a password hash, use the built-in `Pbkdf2HashGenerator` utility or generate one with .NET's `KeyDerivation.Pbkdf2`. Replace the `PasswordHash` value in `appsettings.json` before deploying.

For production use, replace the simple cookie auth with Windows Authentication, Azure Entra ID (OIDC), or another enterprise provider.

### Rate Limiting

API endpoints are protected by ASP.NET Core rate limiting: 120 requests per IP per minute with a queue of 10. Adjust in `Program.cs`.

### No Secrets in Code

Connection strings and user credentials must come from `appsettings.Development.json` (local dev) or environment variables / Azure Key Vault / secrets manager (production). **Never commit real credentials.** See [Setup](#setup) for guidance.

---

## Project Structure

```
DbExplorer.sln
├── DbExplorer.Core/               # DTOs, interfaces, validation helpers (no ASP.NET dependency)
│   ├── Models/Models.cs
│   ├── Interfaces/IServices.cs
│   └── Validation/SqlIdentifierHelper.cs
├── DbExplorer/                    # ASP.NET Core Blazor Web Application
│   ├── Controllers/               # Thin API controllers (metadata, data)
│   ├── Services/                  # Business logic
│   │   ├── MetadataService.cs
│   │   ├── DataBrowsingService.cs
│   │   ├── AdHocQueryService.cs   # Read-only ad-hoc SQL execution + EnsureReadOnly guard
│   │   ├── QueryBuilderService.cs # Compiles QueryGraph → SQL
│   │   ├── QueryProfilerService.cs# Per-circuit ring buffer of recent queries
│   │   ├── DiagramInteropService.cs# Bridges Blazor diagram widget events to page callbacks
│   │   ├── AuthServices.cs
│   │   └── SqlConnectionFactory.cs
│   ├── Options/                   # Strongly-typed configuration sections
│   │   ├── DataBrowsingOptions.cs
│   │   ├── QueryBuilderOptions.cs # Enabled feature flag
│   │   └── ProfilerOptions.cs     # EnableQueryEditor, EnableSyntaxHighlighting feature flags
│   ├── Components/                # Blazor components
│   │   ├── Layout/                # MainLayout, ThemeToggle
│   │   ├── Pages/                 # Home, ExplorerPage, Login, ProfilerPage, QueryBuilderPage
│   │   ├── Diagram/               # Z.Blazor.Diagrams node model (TableDiagramNode) and widget
│   │   └── Panels/                # ColumnsPanel, IndexesPanel, ForeignKeysPanel,
│   │                              # DefinitionPanel, TriggersPanel
│   ├── wwwroot/css/app.css
│   └── appsettings.json
└── DbExplorer.Tests/              # xUnit tests
    ├── Unit/                      # Unit tests (no I/O)
    └── Integration/               # Integration tests using WebApplicationFactory + Mocks
```

---

## Setup

### Prerequisites

- .NET 10 SDK
- One or more of: SQL Server 2016+, MySQL 5.7+, PostgreSQL 12+

### 1. Configure connection strings

Copy `DbExplorer/appsettings.Development.example.json` to `DbExplorer/appsettings.Development.json` (this file is gitignored) and fill in your credentials:

```json
{
  "ConnectionStrings": {
    "MySql":     "Server=localhost;Port=3306;Database=mydb;User ID=dbexplorer_ro;Password=SECRET;",
    "PostgreSql":"Host=localhost;Database=mydb;Username=dbexplorer_ro;Password=SECRET;",
    "SqlServer": "Server=localhost;Database=MyDb;User Id=dbexplorer_ro;Password=SECRET;TrustServerCertificate=True;"
  }
}
```

Alternatively, use `dotnet user-secrets` to avoid storing credentials in files:

```bash
cd DbExplorer
dotnet user-secrets set "ConnectionStrings:MySql" "Server=...;Password=SECRET;"
```

### 2. Configure databases and users

Edit `DbExplorer/appsettings.json` (the non-secret parts):

```json
{
  "DbExplorer": {
    "Databases": [
      { "Name": "My Database", "Provider": "MySql", "ConnectionStringName": "MySql" }
    ],
    "Users": [
      { "Username": "admin", "PasswordHash": "pbkdf2:<base64-salt>:<base64-hash>" }
    ]
  }
}
```

To generate a PBKDF2 hash for a password, run:

```bash
dotnet run --project DbExplorer -- hash "YourPassword"
```

### 3. Run the application

```bash
cd DbExplorer
dotnet run
```

Navigate to `http://localhost:5000` and sign in.

### 4. Run tests

```bash
dotnet test DbExplorer.sln
```

### 5. Feature flags

| Flag | Default | Description |
|------|---------|-------------|
| `Profiler:EnableQueryEditor` | `true` | Show/hide the ad-hoc SQL editor panel on the Profiler page |
| `Profiler:EnableSyntaxHighlighting` | `true` | Load CodeMirror/highlight.js from CDN for syntax highlighting; disable for air-gapped environments |
| `QueryBuilder:Enabled` | `true` | Show/hide the Query Builder page and nav link |
| `Mcp:Enabled` | `false` | Enable the MCP server endpoint at `/mcp` |
| `Mcp:ApiKey` | `""` | Required Bearer token for MCP requests; leave empty to disable auth (not recommended) |

---

## MCP Server (Model Context Protocol)

DbExplorer includes an optional read-only MCP server that exposes database exploration tools to AI assistants (GitHub Copilot, Claude Desktop, etc.).

### Enabling

Set `Mcp:Enabled = true` and `Mcp:ApiKey = "<strong-random-secret>"` in `appsettings.json` (or via secrets/environment variables):

```json
{
  "Mcp": {
    "Enabled": true,
    "ApiKey": "your-secret-token-here"
  }
}
```

The MCP endpoint is available at `<app-url>/mcp` (e.g. `https://localhost:2027/mcp`).

### Available tools

| Tool | Description |
|---|---|
| `ListSchemas` | List all schemas in the current database |
| `ListObjects` | List tables, views, and procedures in a schema |
| `GetColumns` | Get column metadata (type, nullability, PK) for a table or view |
| `GetIndexes` | Get index definitions for a table |
| `GetForeignKeys` | Get foreign key relationships for a table |
| `GetDefinition` | Get DDL source for a view, procedure, or function |
| `RunSelectQuery` | Execute a read-only SELECT statement (max 500 rows) |

### Security

- All MCP requests require `Authorization: Bearer <ApiKey>` header
- Only `SELECT`, `WITH`, `SHOW`, `EXPLAIN`, `DESCRIBE`, and `DESC` statements are allowed — the same read-only guard used by the Profiler's SQL editor
- The MCP endpoint shares the app's rate limiting policy (120 req/min per IP)
- The endpoint is not registered at all when `Mcp:Enabled = false`

### MCP client configuration

For GitHub Copilot or Claude Desktop, add to your MCP config:

```json
{
  "mcpServers": {
    "dbexplorer": {
      "url": "https://localhost:2027/mcp",
      "headers": {
        "Authorization": "Bearer your-secret-token-here"
      }
    }
  }
}
```

---

## Query Builder

The Query Builder page (at `/query-builder`) generates read-only `SELECT` queries from a visual interface. It can be enabled/disabled via `QueryBuilder:Enabled` in `appsettings.json`.

### Visual Canvas

Drag tables from the left-hand Explorer tree onto the canvas. Once on the canvas:

- **Move** a table node by dragging its header
- **Remove** a table node with the ✕ button in the header
- **Select columns** with the checkboxes on each column row
- **Create a JOIN** by dragging from a right-side port dot (●) on one table's column to a left-side port dot on another table's column — the SQL JOIN is generated automatically
- **Change JOIN type** in the join config panel that appears below the canvas

Multiple links between the same table pair are deduplicated to a single JOIN clause.

### Form Builder

The Form Builder provides a step-by-step panel interface:

1. Choose a base schema and table
2. Select columns
3. Add JOINs (with schema, table, and column mapping)
4. Add WHERE filters
5. Choose ORDER BY and row limit

SQL is recompiled live as you make changes.

---

## API Reference

All API endpoints require authentication. Unauthenticated requests receive `401`.

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/metadata/schemas` | List all schemas containing objects |
| GET | `/api/metadata/objects?schema=dbo` | List objects (optionally filtered by schema) |
| GET | `/api/metadata/columns?schema=dbo&objectName=Users` | Get column metadata |
| GET | `/api/metadata/indexes?schema=dbo&tableName=Users` | Get indexes |
| GET | `/api/metadata/foreignkeys?schema=dbo&tableName=Orders` | Get foreign keys |
| GET | `/api/metadata/definition?schema=dbo&objectName=MyView` | Get object source definition |
| GET | `/api/data/page?schema=dbo&objectName=Users&page=1&pageSize=50` | Get paged rows |
| GET | `/api/data/export-csv?schema=dbo&objectName=Users&page=1&pageSize=500` | Download page as CSV |

---

## Limitations

- The application is read-only. Stored procedures cannot be executed from the UI.
- Object definitions are only available for views, procedures, functions, and triggers (not tables — use the Columns tab).
- Very large tables (billions of rows) will have accurate `COUNT_BIG(*)` but may return slowly; the 500-row page cap limits data transfer.
- The CSV export only exports the currently paged result, not the full table.
- The login page uses a simple PBKDF2-based credential store. For enterprise use, replace with OIDC/Windows Auth.

---

## Development Notes

- All database calls pass `CancellationToken` propagated from HTTP request contexts.
- All connections are opened per-request via `SqlConnectionFactory` (no connection pooling singleton).
- Dapper is used for lightweight mapping of catalog query results. No ORM writes.
- `ProblemDetails` responses are returned for all API errors.
- Serilog writes structured logs to console and rolling daily files in `logs/`.
