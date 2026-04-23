# DbExplorer

A production-grade, read-only SQL Server database explorer built on .NET 10 ASP.NET Core with a Blazor Server frontend.

## Features

- Dynamic schema discovery at runtime — no prior knowledge of the target database required
- Browse schemas, tables, views, stored procedures, and functions
- View columns (with types, nullability, defaults, PK membership), indexes, and foreign keys
- Read object definitions (views, procs, functions) in a source viewer
- Pageable data grid with server-side paging (max 500 rows/page, default 50)
- CSV export of the current page
- Object name search/filter in the left-hand tree
- Rate limited API (120 req/min per IP)
- Cookie-based authentication

---

## Security Architecture

### Read-Only by Design

The application **only ever executes** `SELECT` queries and read-only catalog queries. There are no endpoints, pages, or service methods that issue `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `DROP`, `ALTER`, `CREATE`, `EXEC`, or accept arbitrary SQL from the user.

### Identifier Validation

Every schema and object name passes through two layers of validation before use in SQL:

1. **Static format check** (`SqlIdentifierHelper.IsValidIdentifierFormat`) — rejects names containing spaces, SQL metacharacters, or names exceeding 128 characters.
2. **Live catalog check** (`IIdentifierValidator.ValidateObjectAsync`) — parameterized-query lookup against `sys.objects` / `sys.schemas` to confirm the name exists. Only after both checks pass does the service bracket-quote the identifier with `SqlIdentifierHelper.Quote` and interpolate it into a fixed, read-only SQL template.

User-supplied values (page number, page size, search filter) are **never** interpolated into SQL — they are passed as Dapper parameters.

### Least-Privilege SQL Account

The SQL login used by DbExplorer should have:

```sql
-- Create a dedicated login
CREATE LOGIN dbexplorer_ro WITH PASSWORD = '<strong password>';
USE YourDatabase;
CREATE USER dbexplorer_ro FOR LOGIN dbexplorer_ro;

-- Minimum permissions
EXEC sp_addrolemember 'db_datareader', 'dbexplorer_ro';
GRANT VIEW DEFINITION TO dbexplorer_ro;
```

The application does not need `db_owner`, `db_ddladmin`, or any write permissions.

### Authentication

DbExplorer uses ASP.NET Core cookie authentication. Users are configured in `appsettings.json` with PBKDF2-hashed passwords. **Replace the placeholder hash** before deployment:

```csharp
// Generate a PBKDF2 hash for a password:
var hash = BCryptHelper.Hash("YourPassword");
Console.WriteLine(hash);
```

For production use, replace the simple cookie auth with Windows Authentication, Azure Entra ID (OIDC), or another enterprise provider.

### Rate Limiting

API endpoints are protected by ASP.NET Core rate limiting: 120 requests per IP per minute with a queue of 10. Adjust in `Program.cs`.

### No Secrets in Code

Connection strings and user credentials must come from `appsettings.json` (development) or environment variables / Azure Key Vault / secrets manager (production). Never commit real credentials.

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
│   ├── Services/                  # Business logic (metadata, data browsing, auth, connection)
│   ├── Components/                # Blazor components
│   │   ├── Layout/                # MainLayout
│   │   ├── Pages/                 # Home, ExplorerPage
│   │   └── Panels/                # ColumnsPanel, IndexesPanel, ForeignKeysPanel, DefinitionPanel
│   ├── wwwroot/css/app.css        # Application styles
│   └── appsettings.json
└── DbExplorer.Tests/              # xUnit tests
    ├── Unit/                      # Unit tests (no I/O)
    └── Integration/               # Integration tests using WebApplicationFactory + Mocks
```

---

## Setup

### Prerequisites

- .NET 10 SDK
- SQL Server 2016+ (or Azure SQL)

### 1. Configure the connection string

Edit `DbExplorer/appsettings.json` (or use user secrets / environment variables):

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=myserver;Database=mydb;User Id=dbexplorer_ro;Password=SECRET;TrustServerCertificate=True;"
  }
}
```

### 2. Configure a user

```json
{
  "DbExplorer": {
    "Users": [
      {
        "Username": "admin",
        "PasswordHash": "pbkdf2:<base64-salt>:<base64-hash>"
      }
    ]
  }
}
```

Generate a hash by running the app in debug mode and calling `BCryptHelper.Hash("YourPassword")`.

### 3. Run the application

```bash
cd DbExplorer
dotnet run
```

Navigate to `https://localhost:5001` and sign in.

### 4. Run tests

```bash
dotnet test DbExplorer.sln
```

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
- Object definitions are only available for views, procedures, and functions (not tables — use the Columns tab instead).
- Very large tables (billions of rows) will have accurate `COUNT_BIG(*)` but may return slowly; the 500-row page cap limits data transfer.
- The CSV export only exports the currently paged result, not the full table.
- The login page uses a simple PBKDF2-based credential store. For enterprise use, replace with OIDC/Windows Auth.

---

## Development Notes

- All database calls pass `CancellationToken` propagated from HTTP request contexts.
- All connections are opened per-request via `SqlConnectionFactory` (no connection pooling singleton).
- `Dapper` is used for lightweight mapping of catalog query results. No ORM writes.
- `ProblemDetails` responses are returned for all API errors.
- `Serilog` writes structured logs to console and rolling daily files in `logs/`.
