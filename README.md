# DbExplorer

> **A safe, self-hosted database explorer for teams that can't — or won't — give everyone direct database access.**

DbExplorer is a read-only web UI for SQL Server, MySQL, and PostgreSQL. Drop it in front of any database and give your team (developers, analysts, support engineers) a polished interface to browse schemas, run queries, and build `SELECT` statements — without handing out connection strings or risking accidental writes.

Built with .NET 10 Blazor Server. No cloud dependency. No data leaves your network.

---

## Why DbExplorer?

| Problem | How DbExplorer helps |
|---------|----------------------|
| You have databases in production that devs need to inspect but shouldn't have raw access to | Single, auditable endpoint with read-only enforcement at every layer |
| Non-technical stakeholders need to explore data without learning SQL | Visual Query Builder — drag tables, draw JOINs, get SQL instantly |
| You want to give AI assistants (Copilot, Claude) access to your schema without risk | Built-in MCP server exposes schema and query tools behind a Bearer token |
| Running ad-hoc queries against production is scary | The Profiler page only allows `SELECT` statements — DDL and DML are blocked server-side |
| You need GDPR-compliant access logging | Optional structured audit log records sign-ins, sign-outs, and every data access with who/what/when — routable to any Serilog sink |
| You want SSO without running your own identity server | Windows Negotiate (Kerberos) and Google OAuth built-in, both feature-flagged |
| Dark mode | Yes |

---

## Features

### Schema Explorer
- Browse **schemas, tables, views, stored procedures, functions, and triggers** in a live tree
- View **columns** (type, nullability, default, primary key), **indexes**, **foreign keys**, and object **source definitions**
- **Pageable data grid** — server-side paging up to 500 rows, column sorting, CSV export
- **Recently-viewed** tracking and **object name search/filter**

### Visual Query Builder
Build `SELECT` queries without writing SQL:
- **Visual Canvas** — drag tables onto a diagram, draw column-to-column JOIN links by connecting port dots; SQL is compiled live as you work
- **Form Builder** — step-by-step panel for selecting tables, JOINs, columns, filters, sorting, and row limits

### Query Profiler
- **Ad-hoc SQL editor** — run any `SELECT` (DML/DDL automatically rejected)
- **EXPLAIN plan** viewer
- **Live server activity** monitor (active connections/queries)
- **Per-session query history** and **recent query statistics** (`pg_stat_statements` on PostgreSQL)

### MCP Server (AI Integration)
Expose your database schema and query execution to AI assistants via the [Model Context Protocol](https://modelcontextprotocol.io/):
- 7 read-only tools: `ListSchemas`, `ListObjects`, `GetColumns`, `GetIndexes`, `GetForeignKeys`, `GetDefinition`, `RunSelectQuery`
- Bearer token authentication
- Disabled by default — opt-in per deployment

### Authentication
- **Built-in credential store** — username + PBKDF2-hashed password in `appsettings.json`. Controlled by `Auth:Local:Enabled` (default `true`; cannot be disabled unless at least one external provider is enabled)
- **Windows Authentication (Negotiate/Kerberos)** — domain users sign in with one click, no password typed; disabled by default, enable with `Auth:Windows:Enabled = true`
- **Google OAuth 2.0** — sign in with a Google account; optional email allow-list restricts access to specific domains (`*@yourcompany.com`) or individuals; disabled by default, enable with `Auth:Google:Enabled = true`
- All providers issue the same secure session cookie after sign-in — a single auth scheme protects the whole app regardless of which provider was used

### Other
- Dark mode / light mode toggle
- Multi-database support — connect multiple SQL Server, MySQL, and PostgreSQL instances side-by-side
- Rate-limited API (120 req/min per IP)

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

The `EnsureReadOnly` guard in `AdHocQueryService` strips both block and line comments **and single-quoted string literals** from submitted SQL, then:

1. Rejects multi-statement batches (`;` separator)
2. Requires the statement to begin with `SELECT`, `WITH` (CTE), `SHOW`, `EXPLAIN`, `DESCRIBE`, or `DESC`
3. Scans the full text for write-DML keywords (`INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE`, `EXEC`, `EXECUTE`, `MERGE`, `TRUNCATE`, `CALL`, `LOAD`, `COPY`) — also blocks writable CTEs

Stripping string literals before scanning prevents false-positive rejections where a keyword appears only inside a quoted value (e.g. `SELECT 'LOAD DATA' AS msg` is valid read-only SQL and is permitted).

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

DbExplorer supports three authentication mechanisms, all of which issue a secure ASP.NET Core session cookie after sign-in:

1. **Built-in credential store** — username + PBKDF2-hashed password configured in `appsettings.json`. Suitable for small teams or personal deployments. Controlled by `Auth:Local:Enabled` (default `true`; automatically stays on when no external provider is active to prevent lockout).
2. **Windows Authentication (Negotiate/Kerberos)** — domain users sign in with a single click via their Windows credentials. No password typed. Enable with `Auth:Windows:Enabled = true`. See [External Authentication](#external-authentication).
3. **Google OAuth 2.0** — sign in with a Google account, optionally restricted to specific email domains. Enable with `Auth:Google:Enabled = true`. See [External Authentication](#external-authentication).

All three providers can coexist — the login page shows only the buttons for providers that are enabled.

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
│   ├── Controllers/               # Thin API controllers (metadata, data, external auth)
│   ├── Services/                  # Business logic
│   │   ├── MetadataService.cs
│   │   ├── DataBrowsingService.cs
│   │   ├── AdHocQueryService.cs   # Read-only ad-hoc SQL execution + EnsureReadOnly guard
│   │   ├── QueryBuilderService.cs # Compiles QueryGraph → SQL
│   │   ├── QueryProfilerService.cs# Per-circuit ring buffer of recent queries
│   │   ├── DiagramInteropService.cs# Bridges Blazor diagram widget events to page callbacks
│   │   ├── AuthServices.cs        # Local PBKDF2 credential login handler
│   │   ├── AuditLoggerService.cs  # Singleton structured audit log writer
│   │   └── SqlConnectionFactory.cs
│   ├── Options/                   # Strongly-typed configuration sections
│   │   ├── DataBrowsingOptions.cs
│   │   ├── QueryBuilderOptions.cs # Enabled feature flag
│   │   ├── ProfilerOptions.cs     # EnableQueryEditor, EnableSyntaxHighlighting feature flags
│   │   ├── AuthOptions.cs         # Windows + Google external auth feature flags
│   │   ├── AuditOptions.cs        # GDPR audit logging feature flag + LogSql toggle
│   │   └── McpOptions.cs          # MCP server feature flag + ApiKey
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

To generate a PBKDF2 hash for a new password, add a temporary line to `Program.cs` before `var builder = ...`:

```csharp
// Temporary — remove after generating hash
if (args.Length > 0 && args[0] == "hash")
{
    Console.WriteLine(DbExplorer.Services.BCryptHelper.Hash(args[1]));
    return;
}
```

Then run:

```bash
dotnet run --project DbExplorer hash "YourPassword"
```

Copy the printed `pbkdf2:...` string into the `PasswordHash` field in `appsettings.json`, then remove the temporary code. The format is `pbkdf2:<base64-salt>:<base64-hash>` with 350,000 PBKDF2-SHA256 iterations.

### 3. Run the application

```bash
dotnet run --project DbExplorer
```

Navigate to `https://localhost:2027` (or `http://localhost:2028`) and sign in. The port is configured in `DbExplorer/Properties/launchSettings.json`.

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
| `Mcp:ApiKey` | `""` | **Required** Bearer token when MCP is enabled; the endpoint returns HTTP 503 until a value is set |
| `Audit:Enabled` | `false` | Enable GDPR audit logging — records who accessed what and when (no row data) |
| `Audit:LogSql` | `true` | Include SQL text in audit records for ad-hoc queries and MCP `RunSelectQuery` calls; set `false` if users may embed PII in predicates |
| `Auth:Local:Enabled` | `true` | Enable the built-in username/password login form; automatically stays active when no external providers are enabled (lockout prevention) |
| `Auth:Windows:Enabled` | `false` | Enable Windows Negotiate (Kerberos/NTLM) sign-in; requires a domain-joined server |
| `Auth:Google:Enabled` | `false` | Enable Google OAuth 2.0 sign-in; requires `ClientId` and `ClientSecret` from Google Cloud Console |

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

- **`Mcp:ApiKey` is mandatory** — enabling MCP without setting an `ApiKey` will cause all requests to be rejected with HTTP 503 and a startup warning logged to the console. There is no unauthenticated mode.
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

## Audit Logging (GDPR)

Enable structured access logging for compliance and incident response by setting `Audit:Enabled = true`:

```json
{
  "Audit": {
    "Enabled": true,
    "LogSql": true
  }
}
```

### What is logged

Every auditable operation emits a structured log event under the `DbExplorer.Audit` logger category:

| Field | Description |
|-------|-------------|
| `Action` | `MetadataAccess`, `DataAccess`, `AdHocQuery`, `McpToolCall`, `Login`, `LoginFailed`, or `Logout` |
| `Username` | Authenticated user (or the attempted username for `LoginFailed`) |
| `SchemaName` | Schema involved (if applicable, else `-`) |
| `ObjectName` | Table/view/object accessed (if applicable, else `-`) |
| `RowCount` | Number of rows returned (`-1` for non-data events) |
| `ElapsedMs` | Query duration (`-1` for non-query events) |
| `Sql` | SQL text for ad-hoc queries and MCP `RunSelectQuery` calls (controlled by `Audit:LogSql`) |
| `Context` | Provider context for authentication events (e.g. `{ "provider": "google" }`); tool name for MCP calls (e.g. `{ "tool": "RunSelectQuery" }`) |

**No row data is ever logged.** Only access and authentication metadata is recorded.

### Log routing

Audit events are written via `ILogger` and are fully compatible with Serilog. To write them to a separate sink, filter by the `SourceContext` in your Serilog configuration:

```json
{
  "Serilog": {
    "Override": {
      "DbExplorer.Services.AuditLoggerService": "Information"
    },
    "WriteTo": [
      { "Name": "File", "Args": { "path": "logs/audit-.log", "rollingInterval": "Day" } }
    ]
  }
}
```

### GDPR note

SQL statements from ad-hoc queries and MCP tool calls are included in audit logs because they are operational metadata. Review your data classification policy before enabling if users may embed personal data in query predicates (e.g. `WHERE email = 'user@example.com'`). Set `Audit:LogSql = false` to record all other access metadata without capturing the SQL text.

---

## External Authentication

Beyond the built-in username/password login, DbExplorer supports two additional authentication providers, both feature-flagged and disabled by default.

### Windows Authentication (Negotiate/Kerberos)

For enterprise environments on a Windows domain, users can sign in with their domain credentials via the "Sign in with Windows" button on the login page. The server negotiates via Kerberos or NTLM — no password is typed. Set `Auth:Local:Enabled = false` to hide the password form when all users are on the domain.

**Requirements**: IIS with Windows Authentication enabled, or Kestrel running on a domain-joined server.

```json
{
  "Auth": {
    "Windows": { "Enabled": true }
  }
}
```

### Google OAuth 2.0

Allow users to sign in with their Google account. Optionally restrict access to specific email addresses or entire domains using wildcard patterns. Set `Auth:Local:Enabled = false` to remove the password form entirely when all users authenticate via Google.

**Setup**:
1. Create an OAuth 2.0 client in [Google Cloud Console](https://console.cloud.google.com/apis/credentials).
2. Add `https://<your-app>/signin-google` as an authorised redirect URI.
3. Store the client secret in [user secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) or an environment variable — **never commit it to source control**.

```json
{
  "Auth": {
    "Google": {
      "Enabled": true,
      "ClientId": "YOUR_CLIENT_ID.apps.googleusercontent.com",
      "ClientSecret": "stored-in-user-secrets-or-env-var",
      "AllowList": [
        "*@yourcompany.com",
        "contractor@partner.com"
      ]
    }
  }
}
```

#### AllowList patterns

| Pattern | Meaning |
|---------|---------|
| `*@yourcompany.com` | Anyone with a `yourcompany.com` Google account |
| `*@*.yourcompany.com` | Any sub-domain of `yourcompany.com` (greedy — also matches deeper levels like `a.b.yourcompany.com`) |
| `alice@gmail.com` | Exact match — one specific account |
| *(empty list)* | Any authenticated Google account is allowed |

> **Note**: `*` is a greedy wildcard that matches dots and hyphens. If you need to restrict to a single sub-domain level, list each allowed sub-domain explicitly (e.g. `*@app.yourcompany.com`, `*@api.yourcompany.com`).

---

## Query Builder

The Query Builder page (at `/query-builder`) generates read-only `SELECT` queries from a visual interface. It can be enabled/disabled via `QueryBuilder:Enabled` in `appsettings.json`.

### Visual Canvas

Drag tables from the left-hand Explorer tree onto the canvas. Once on the canvas:

- **Move** a table node by dragging its header
- **Remove** a table node with the ✕ button in the header
- **Select columns** with the checkboxes on each column row
- **Create a JOIN** by dragging from a right-side port dot (●) on one table's column to a left-side port dot on another table's column — the SQL JOIN is generated automatically. **Join direction is normalised automatically** — you can start the drag from either table and the SQL will be correct.
- **Remove a JOIN** by clicking the ✕ button on the join row in the JOIN Configuration panel below the canvas, or by deleting the link from the canvas
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
- The login page uses a PBKDF2-based credential store by default. For enterprise use, Windows Authentication and Google OAuth are available as feature-flagged options — see [External Authentication](#external-authentication).

---

## Development Notes

- All database calls pass `CancellationToken` propagated from HTTP request contexts.
- All connections are opened per-request via `SqlConnectionFactory` (no connection pooling singleton).
- Dapper is used for lightweight mapping of catalog query results. No ORM writes.
- `ProblemDetails` responses are returned for all API errors.
- Serilog writes structured logs to console and rolling daily files in `logs/`.

---

## Known Limitations & Future Work

### Test Coverage Gaps

The following areas have no automated tests:

- **`DbExplorerMcpTools`** — all 7 MCP tools are untested. Unit tests should mock `IMetadataService`, `IAdHocQueryService`, and `IAuditLogger`, and verify output serialization, read-only enforcement error propagation, and row-count headers.
- **`AuditLoggerService`** — no unit test; verify the no-op behaviour when `Enabled = false` and the log message format when `Enabled = true`.
- **MCP bearer token middleware** — no integration test covers the 401/503 paths. An integration test using `WebApplicationFactory` with `Mcp:Enabled = true` and varying `Authorization` headers would cover the guard logic.

### Minor Design Notes

- The `DiagramInteropService` callbacks (`OnNodeRemove`, `OnGraphChanged`) are synchronous `Action` delegates. If removal ever becomes async (e.g. server-side confirmation), they would need to be upgraded to `Func<…, Task>`.
- Canvas `JOIN` deduplication uses the first link per table pair. If two links exist for the same pair, the second is silently dropped. A future version could surface this as a validation warning in the UI.
- `RunSelectQuery` MCP tool row cap (500 rows) is hard-coded. A `Mcp:MaxRows` config option could be added if operators need to tune it.
- The `/api/login` endpoint is currently covered only by the global 120 req/min rate limiter. A dedicated low-count limiter (e.g. 5 req/min per IP) would further reduce brute-force risk.
