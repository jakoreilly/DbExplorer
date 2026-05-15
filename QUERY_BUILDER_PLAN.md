# Visual Query Builder — Design & Implementation Plan

## 1. Concept

A structured, form-driven query builder that lets users compose SQL visually (table selection → column selection → JOINs → WHERE filters) and see live-generated, provider-correct SQL via SqlKata. Phase 2 upgrades to a draggable node canvas via Blazor.Diagrams.

### What it is NOT (for v1)
- Not a drag-drop node canvas (deferred to Phase 2)
- Not a SQL parser / bidirectional sync (complex, error-prone)
- Not a full ORM (CTEs, subqueries, window functions excluded from MVP)

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│ QueryBuilderPage.razor                                          │
│  ┌──────────────┐  ┌───────────────┐  ┌─────────────────────┐  │
│  │ Table Panel  │  │ Join Panel    │  │  Filter Panel       │  │
│  │ schema+table │  │ add/remove    │  │  col + op + value   │  │
│  │ col checks   │  │ join type     │  │                     │  │
│  └──────┬───────┘  └───────┬───────┘  └──────────┬──────────┘  │
│         └──────────────────┴─────────────────────┘             │
│                             │ QueryGraph                        │
│                             ▼                                   │
│              QueryBuilderService.CompileAsync()                 │
│                 (uses SqlKata, respects provider)               │
│                             │                                   │
│  ┌────────────────┐         │                                   │
│  │  SQL Preview   │◄────────┘                                   │
│  │  (code block)  │                                             │
│  └────────────────┘                                             │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Results Grid (reuses DataGrid component)                │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
         │ IAdHocQueryService.ExecuteQueryAsync(sql)
         ▼
   DbConnectionFactory (existing)
```

### Existing infrastructure reused
| Existing piece | Reused for |
|---|---|
| `IMetadataService.GetObjectsAsync` | Populate table/schema selectors |
| `IMetadataService.GetColumnsAsync` | Populate column checkboxes |
| `IMetadataService.GetForeignKeysAsync` | Auto-suggest join conditions |
| `IAdHocQueryService.ExecuteQueryAsync` | Run compiled SQL |
| `DatabaseSelectorState` | Know active provider for correct SqlKata compiler |
| `DataGrid.razor` | Display results (no new grid component needed) |
| `QueryProfiler` | Records executed queries to App Query Log |
| `QueryBuilderOptions.Enabled` | Feature flag — mirrors `ProfilerOptions.EnableQueryEditor` |

---

## 3. New Files

| File | Purpose |
|---|---|
| `DbExplorer.Core/Models/QueryBuilderModels.cs` | `QueryGraph`, `QueryNode`, `NodeEdge`, `JoinType`, `FilterOperator` |
| `DbExplorer.Core/Interfaces/IQueryBuilderService.cs` | `CompileAsync(QueryGraph)` → `(string sql, string[] paramNames)` |
| `DbExplorer/Services/QueryBuilderService.cs` | SqlKata-based implementation |
| `DbExplorer/Options/QueryBuilderOptions.cs` | `Enabled` flag |
| `DbExplorer/Components/Pages/QueryBuilderPage.razor` | UI page |

### Modified files
| File | Change |
|---|---|
| `DbExplorer/Program.cs` | Register `QueryBuilderOptions` + `IQueryBuilderService` |
| `DbExplorer/Components/Layout/MainLayout.razor` | Add nav link |
| `DbExplorer/appsettings.json` | Add `"QueryBuilder": { "Enabled": true }` |
| `DbExplorer/appsettings.Development.example.json` | Same |
| `DbExplorer.csproj` | Add `SqlKata` package reference |

---

## 4. Data Models

```csharp
// DbExplorer.Core/Models/QueryBuilderModels.cs

public enum JoinType { Inner, Left, Right, Full }

public enum FilterOperator
{
    Equals, NotEquals,
    GreaterThan, GreaterThanOrEqual,
    LessThan, LessThanOrEqual,
    Like, NotLike,
    IsNull, IsNotNull
}

/// <summary>One table in the FROM/JOIN list.</summary>
public record QueryTableNode(
    string Id,           // unique within query, e.g. "t1"
    string SchemaName,
    string TableName,
    IReadOnlyList<string> SelectedColumns  // empty = SELECT *
);

/// <summary>A JOIN between two table nodes.</summary>
public record QueryJoinEdge(
    JoinType JoinType,
    string SourceNodeId,    // FK side
    string SourceColumn,
    string TargetNodeId,    // PK side
    string TargetColumn
);

/// <summary>A WHERE filter row.</summary>
public record QueryFilterNode(
    string NodeId,          // which table alias
    string Column,
    FilterOperator Operator,
    string? Value           // null for IS NULL / IS NOT NULL
);

/// <summary>Top-level query descriptor.</summary>
public record QueryGraph(
    IReadOnlyList<QueryTableNode> Tables,
    IReadOnlyList<QueryJoinEdge> Joins,
    IReadOnlyList<QueryFilterNode> Filters,
    int? Limit              // row cap
);
```

---

## 5. Service Interface

```csharp
// DbExplorer.Core/Interfaces/IQueryBuilderService.cs

public interface IQueryBuilderService
{
    /// <summary>
    /// Compiles a QueryGraph to provider-correct SQL using SqlKata.
    /// Returns (sql, bindingParams) — params are positional for display only
    /// since IAdHocQueryService uses raw SQL with values inlined.
    /// </summary>
    (string Sql, IReadOnlyList<object?> Bindings) Compile(QueryGraph graph);
}
```

---

## 6. SqlKata Compiler Service

Key points:
- Resolve compiler from current `DatabaseProvider` via `IDbConnectionFactory.Provider`
- Inline literal values (safe for read-only display; not used in parameterised execution)
- Prefix table aliases (`t0`, `t1`…) to avoid column name collisions across joins
- Guard against empty column list (fallback to `SELECT *`)

```csharp
// DbExplorer/Services/QueryBuilderService.cs

using SqlKata;
using SqlKata.Compilers;

public sealed class QueryBuilderService : IQueryBuilderService
{
    private readonly IDbConnectionFactory _factory;

    public QueryBuilderService(IDbConnectionFactory factory) => _factory = factory;

    private Compiler GetCompiler() => _factory.Provider switch
    {
        DatabaseProvider.PostgreSql  => new PostgresCompiler(),
        DatabaseProvider.MySql       => new MySqlCompiler(),
        DatabaseProvider.SqlServer   => new SqlServerCompiler(),
        _                            => new SqlServerCompiler()
    };

    public (string Sql, IReadOnlyList<object?> Bindings) Compile(QueryGraph graph)
    {
        if (graph.Tables.Count == 0)
            return ("-- Add a table to start", Array.Empty<object?>());

        var baseTable = graph.Tables[0];
        var alias = baseTable.Id;
        var fromExpr = $"{Q(baseTable.SchemaName)}.{Q(baseTable.TableName)} AS {alias}";

        var query = new Query(fromExpr);

        // SELECT columns
        var cols = BuildSelectColumns(graph);
        if (cols.Count > 0)
            query.Select(cols.ToArray());
        else
            query.SelectRaw("*");

        // JOINs
        foreach (var join in graph.Joins)
        {
            var target = graph.Tables.First(t => t.Id == join.TargetNodeId);
            var targetAlias = join.TargetNodeId;
            var targetExpr = $"{Q(target.SchemaName)}.{Q(target.TableName)} AS {targetAlias}";

            var onLeft  = $"{join.SourceNodeId}.{Q(join.SourceColumn)}";
            var onRight = $"{targetAlias}.{Q(join.TargetColumn)}";

            query = join.JoinType switch
            {
                JoinType.Left  => query.LeftJoin(targetExpr, onLeft, onRight),
                JoinType.Right => query.RightJoin(targetExpr, onLeft, onRight),
                JoinType.Full  => query.Join(targetExpr, j => j.On(onLeft, onRight), "FULL OUTER"),
                _              => query.Join(targetExpr, onLeft, onRight)
            };
        }

        // WHERE filters
        foreach (var f in graph.Filters)
            ApplyFilter(query, f);

        // LIMIT
        if (graph.Limit.HasValue)
            query.Limit(graph.Limit.Value);

        var compiler = GetCompiler();
        var result   = compiler.Compile(query);
        return (result.Sql, result.Bindings);
    }

    private static List<string> BuildSelectColumns(QueryGraph graph)
    {
        var list = new List<string>();
        foreach (var t in graph.Tables)
        {
            foreach (var col in t.SelectedColumns)
                list.Add($"{t.Id}.{Q(col)}");
        }
        return list;
    }

    private static void ApplyFilter(Query query, QueryFilterNode f)
    {
        var col = $"{f.NodeId}.{Q(f.Column)}";
        switch (f.Operator)
        {
            case FilterOperator.Equals:              query.Where(col, f.Value); break;
            case FilterOperator.NotEquals:           query.WhereNot(col, f.Value); break;
            case FilterOperator.GreaterThan:         query.Where(col, ">",  f.Value); break;
            case FilterOperator.GreaterThanOrEqual:  query.Where(col, ">=", f.Value); break;
            case FilterOperator.LessThan:            query.Where(col, "<",  f.Value); break;
            case FilterOperator.LessThanOrEqual:     query.Where(col, "<=", f.Value); break;
            case FilterOperator.Like:                query.WhereLike(col, f.Value ?? ""); break;
            case FilterOperator.NotLike:             query.WhereNotLike(col, f.Value ?? ""); break;
            case FilterOperator.IsNull:              query.WhereNull(col); break;
            case FilterOperator.IsNotNull:           query.WhereNotNull(col); break;
        }
    }

    /// <summary>Quotes an identifier (provider-agnostic double-quote; SqlKata post-processes).</summary>
    private static string Q(string id) => $"\"{id}\"";
}
```

---

## 7. Feature Flag

```csharp
// DbExplorer/Options/QueryBuilderOptions.cs
namespace DbExplorer.Options;

public sealed class QueryBuilderOptions
{
    public bool Enabled { get; init; } = true;
}
```

appsettings.json addition:
```json
"QueryBuilder": {
  "Enabled": true
}
```

---

## 8. Page UI Layout

```
┌──────────────────────────────────────────────────────────────────┐
│  Query Builder          [connection badge]                       │
├──────────────────────────────────────────────────────────────────┤
│  ▼ 1. Base Table                                                 │
│     Schema: [dropdown]  Table: [dropdown]   [Load Columns]       │
│     Columns: ☑ id  ☑ name  ☐ created_at  ...  [All] [None]      │
├──────────────────────────────────────────────────────────────────┤
│  ▼ 2. Joins                              [+ Add Join]            │
│     [INNER ▼] [t0.col ▼] = [table ▼].[col ▼]     [✕]            │
├──────────────────────────────────────────────────────────────────┤
│  ▼ 3. Filters (WHERE)                    [+ Add Filter]          │
│     [table.col ▼] [= ▼] [value input]                [✕]        │
├──────────────────────────────────────────────────────────────────┤
│  ▼ 4. Options    Limit: [1000]                                   │
├──────────────────────────────────────────────────────────────────┤
│  Generated SQL (live preview, syntax highlighted)                │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │ SELECT t0."id", t0."name"                                  │  │
│  │ FROM "public"."users" AS t0                                │  │
│  │ WHERE t0."active" = true                                   │  │
│  │ LIMIT 1000                                                 │  │
│  └────────────────────────────────────────────────────────────┘  │
│  [▶ Run Query]  [⬇ Copy SQL]                                     │
├──────────────────────────────────────────────────────────────────┤
│  Results                                                         │
│  [DataGrid or results table]                                     │
└──────────────────────────────────────────────────────────────────┘
```

---

## 9. Constraints & What to Avoid

### Security
- **NEVER** pass raw user-input as SQL identifiers. Always select from `MetadataService`-validated lists.
- Use `IAdHocQueryService.ExecuteQueryAsync` (which has row-cap and read-only guard) — never call `DbConnection.Execute` directly from the page.
- SqlKata compiles with parameterised bindings; when inlining for display only — clearly label it "preview" and never execute the raw string-interpolated display version.
- Filter values **are** inlined by SqlKata — safe because they flow through SqlKata's parameterisation, not raw string concat.

### Scope (v1 — do NOT implement)
- ❌ Drag-and-drop node canvas (Phase 2 — Blazor.Diagrams)
- ❌ Bidirectional SQL → graph sync (requires full SQL AST parser)
- ❌ CTEs, subqueries, window functions (complex graph topology)
- ❌ GROUP BY / HAVING / ORDER BY (Phase 2)
- ❌ Schema write operations (INSERT/UPDATE/DELETE)
- ❌ Cross-database queries

### Code quality
- The page should be `@attribute [Authorize]` — query builder has full SELECT access.
- `IQueryBuilderService` is stateless — register as **scoped** (same as other services; it depends on `IDbConnectionFactory`).
- Avoid storing `List<ColumnInfo>` per table in a dictionary keyed by string — use a record/struct key `(schema, table)`.
- `QueryGraph` and its children are **immutable records** — rebuild the graph each time the user changes a selector; do not mutate in-place.

---

## 10. Phase 2 — Visual Node Canvas (Blazor.Diagrams)

After v1 is stable:

```
dotnet add package Blazor.Diagrams --version 3.1.2
dotnet add package Blazor.Diagrams.Core --version 3.1.2
```

1. Register `Blazor.Diagrams` CSS + JS in `App.razor` (behind `QueryBuilderOptions.Enabled`).
2. Define custom node models:
   - `TableNodeModel : NodeModel` — holds schema/table/columns
   - `FilterNodeModel : NodeModel` — holds column/op/value
3. Define custom link model: `JoinLinkModel : LinkModel` — holds `JoinType`
4. Page has two tabs: **Visual Canvas** and **SQL Preview**
5. On canvas change → rebuild `QueryGraph` from diagram state → recompile
6. Auto-layout via `Blazor.Diagrams` layout engine (Dagre via JS interop)

> **Note**: Blazor.Diagrams requires .NET 7+. It supports .NET 10 via netstandard2.0 compatibility. Verify at: https://github.com/Blazor-Diagrams/Blazor.Diagrams

---

## 11. Test Gaps to Fill (after implementation)

| Test | Type | Coverage |
|---|---|---|
| `QueryBuilderService_Compiles_SelectAllColumns` | Unit | SqlKata SELECT * fallback |
| `QueryBuilderService_Compiles_InnerJoin` | Unit | JOIN edge translated |
| `QueryBuilderService_Compiles_Filters_AllOperators` | Unit | All `FilterOperator` enum values |
| `QueryBuilderService_EmptyGraph_ReturnsPlaceholder` | Unit | Edge case: 0 tables |
| `QueryBuilderService_PostgresCompiler_UsedForPostgres` | Unit | Provider-specific SQL |
| Integration test: `/query-builder` page loads | Integration | Auth + render |
