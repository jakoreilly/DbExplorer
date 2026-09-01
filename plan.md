# plan.md — Sonar remediation for commit `ca4a794`

## How to work this plan

Work **phase by phase, in order**. Each phase is sized for one fresh session and ends with a green build and at least one commit. After **every** phase and **every** commit inside a phase, run:

```
dotnet test DbExplorer.sln --nologo -v quiet
```

It must report `Failed: 0` and a passing total that is **greater than or equal to** the count recorded in `continue.md`. The baseline at plan authoring time (commit `ca4a794`) is **190 passed, 0 failed**.

Global rules:

1. **`plan.md` is read-only during implementation.** All state, deviations, and progress go in `continue.md`. If `plan.md` is discovered to be wrong, record the discrepancy in `continue.md` under **Deviations** and stop for human review if the discrepancy invalidates a constraint.
2. **A phase is NOT done** until its `continue.md` handoff note is written and its Progress row says `done` with the DoD result recorded.
3. **Behaviour-preserving extraction first.** Where a phase says "move verbatim", move the code unchanged. Improvements to moved code belong in a later phase as separate commits.
4. **Do not fix things this plan does not name.** The Non-goals section is binding. If you find a new bug, log it in `continue.md` under *Discovered gotchas* and move on.
5. The final phase's DoD includes: every Progress row `done`, zero blocking open questions, and a closing summary appended to `continue.md` listing anything that could not be runtime-verified.

---

## 1. Context

**The system.** DbExplorer is a Blazor Server (net10.0, C# 13) database browser targeting SQL Server, PostgreSQL and MySQL through a common `IDbConnectionFactory` / `SqlDialect` abstraction. Three projects: `DbExplorer` (web app), `DbExplorer.Core` (models, interfaces, pure logic), `DbExplorer.Tests` (xUnit + FluentAssertions + Moq, 190 tests).

**The problem.** A SonarQube-equivalent analysis of commit `ca4a794` ("Add analyzer, metadata search, and diagram enhancements", 1,539 new lines of main code across 27 files) returned:

```
QUALITY GATE: FAILED
  ✗ New issues                     15    >  0
  ✗ Security hotspots reviewed    0/3       not reviewed
  ✓ Duplication on new code      0.0%   ≤  3.0%
  ─ Coverage on new code           —       NOT EVALUATED
```

Ratings on new code: Security **A**, Reliability **C**, Maintainability **A**, Security Review **E**. Technical debt 147 min, debt ratio 0.32%.

### The 15 issues, with evidence

| # | Severity · Quality | Rule | Location | One-line defect |
|---|---|---|---|---|
| 1 | Medium · Reliability | `[unmapped]` dialect-correct literal escaping | `DbExplorer/Services/DataValueSearchService.cs:277` | `'`-doubling only; MySQL also needs `\` escaped, so a term with a backslash breaks or escapes the literal |
| 2 | Medium · Reliability | `[unmapped]` per-item failure isolation | `DbExplorer/Services/DataValueSearchService.cs:95-102` | One inaccessible catalog aborts the whole connection scan |
| 3 | Medium · Reliability | `[unmapped]` flag cleared on failure path | `DbExplorer/Components/Pages/DiagramPage.razor:440-454` | `IsLoadingColumns` never cleared if `GetColumnsAsync` throws → permanent spinner |
| 4 | Medium · Maintainability | **S2930** IDisposables should be disposed | `DbExplorer/Components/Pages/DiagramPage.razor:307-308` | `CancellationTokenSource` cancelled but never disposed, one leaked per keystroke |
| 5 | Medium · Maintainability | **S3776** cognitive complexity | `DbExplorer.Core/Layout/EntityMapLayout.cs:20` | `Compute` scores **21** (threshold 15) |
| 6 | Medium · Maintainability | **S3776** cognitive complexity | `DbExplorer/Components/Pages/DiagramPage.razor:340` | `RebuildMap` scores **21** (threshold 15) |
| 7 | Low · Reliability | `[unmapped]` one edge per relationship | `DbExplorer/Components/Pages/DiagramPage.razor:420-430` | Composite FK draws one arrow per column, stacked on the same two ports |
| 8 | Low · Reliability | `[unmapped]` negative float→int truncation | `DbExplorer.Core/Analysis/AnalyserMath.cs:47-48` | Events in the `(-1, 0)` minute band land in bucket 0 |
| 9 | Low · Reliability | `[unmapped]` unrenderable result state | `DbExplorer/Services/DataValueSearchService.cs:238-254` | Match returned with empty `Samples`; headline count disagrees with visible rows |
| 10 | Low · Reliability | `[unmapped]` discarded faulting Task | `DbExplorer/Components/Pages/DiagramPage.razor:257-267` | DB-switch failure leaves previous catalog's tables on screen, silently |
| 11 | Low · Maintainability | `[unmapped]` cap applied after accumulation | `DbExplorer/Services/MetadataSearchService.cs:69-95` | Up to 60 × 500 = 30,000 hits materialised to return 500 |
| 12 | Low · Maintainability | `[unmapped]` quadratic scan in loop | `DbExplorer/Components/Pages/DiagramPage.razor:403-412` | 4 full scans per node → ~240k `string.Equals` per rebuild |
| 13 | Low · Maintainability | **S2486** generic exceptions ignored | `DbExplorer/Components/Pages/MetadataSearchPage.razor:274` | Bare `catch { }` around JS interop |
| 14 | Low · Maintainability | **S2486** generic exceptions ignored | `DbExplorer/Components/Pages/AnalyserPage.razor` (`CopySqlAsync`) | Second instance of the same pattern |
| 15 | Low · Maintainability | `[unmapped]` member read but never assigned | `DbExplorer/Components/Diagram/EntityMapNode.cs:41` | `Dimmed` never set; `.emap-node--dim` (`app.css:1407`) is unreachable |

### The 3 hotspots

| # | Priority | Category | Anchor | Sensitive fact |
|---|---|---|---|---|
| H1 | High | CWE-778 · OWASP A09:2021 | `DataValueSearchService.cs:66`, `MetadataSearchService.cs:48` | Every other query surface logs to `IAuditLogger`; neither new search service does |
| H2 | Medium | CWE-89 · OWASP A03:2021 | `DataValueSearchService.cs:271-282` | User term concatenated into SQL text as an inline literal |
| H3 | Medium | CWE-200 · OWASP A01:2021 | `ConnectionCatalogHelper.cs:13-34`, `MetadataSearchPage.razor:12` | Search reaches every database on every server; page is bare `[Authorize]` |

### Goals

- **A** — Fix all 15 issues so the gate's *New issues* condition reads 0.
- **B** — Resolve H1: audit-log both search services through the existing `IAuditLogger` pipeline.
- **C** — Resolve H2: dialect-correct literal escaping in generated SQL (same code change as issue 1; H2 is then a documented review decision).
- **D** — Resolve H3: give operators a supported way to restrict the cross-database search surface.
- **E** — Make coverage on new code *measurable*: add `coverlet.collector` and ship unit tests in the same phase as the logic they cover.

All five were approved by the requester. Nothing from the review was excluded.

---

## 2. Non-goals

These are explicitly **out of scope**. A reasonable implementer might assume they are included; they are not.

1. **Upgrading `Microsoft.AspNetCore.Authentication.Negotiate` 10.0.8.** The build emits `NU1903` — two *known high-severity advisories* (`GHSA-2p3q-h3hg-jcqq`, `GHSA-8prm-248r-h957`). This is real and should be ticketed separately, but a transport-auth package bump is not a Sonar remediation and carries its own regression risk. Recorded as a blocking-for-someone-else open question in `continue.md`. **Do not upgrade it in this plan.**
2. **Introducing ASP.NET Core authorization policies or a roles/claims model.** See the worth-it verdict in §3. The repo has zero precedent and this plan deliberately does not create one.
3. **Refactoring `DataValueSearchService` / `MetadataSearchService` to inject `IDbConnectionFactory`.** They `new` up `DbConnectionFactory` internally (`DataValueSearchService.cs:123` and `:205`, `MetadataSearchService.cs:115`). This makes their I/O paths unmockable. That is accepted — see Constraint 7. Do not "fix" it.
4. **Any change to `SqlDialect`** (`DbExplorer/Services/SqlConnectionFactory.cs:294-355`). Its identifier regex is a load-bearing security control. See Constraint 1.
5. **Rewriting the CodeMirror `hint` wrapper** added at `DbExplorer/Components/App.razor:212-250`. It would score ~22 cognitive complexity if it were in a `.js` file, but SonarQube does not analyse `<script>` blocks in `.razor`, so it is not a gate issue. Extracting it to a real `.js` file is a good idea for a future plan.
6. **`compress.ps1` / `compress.cmd`.** No PowerShell analyser exists; out of scope.
7. **Any change to `DataGrid.razor` filter behaviour**, `MetadataService` FK SQL, or the `SystemAnalyserStore` max-age eviction. These changed in `ca4a794` and raised no issues.
8. **Making `AnalyserPage`'s live-refresh timer or `DiagramPage`'s debounce configurable.** Both are hardcoded and both are fine.
9. **Adding `MaxAgeMinutes` to `appsettings.json`.** `AnalyserOptions.MaxAgeMinutes` defaults to 1440, which satisfies its `[Range(15, 10_080)]`, so `ValidateOnStart` passes. Leave it.
10. **Backfilling tests for pre-existing untested code.** Only code this plan touches gets new tests.

---

## 3. Worth-it verdict

**The requested framing of H3 was "gate `/search` behind a named authorization policy." Phase 0 says do something smaller. This plan implements the smaller thing.**

Evidence:

| Fact | Location |
|---|---|
| Authorization is registered bare — no policies, no requirements | `DbExplorer/Program.cs:78` — `builder.Services.AddAuthorization();` |
| No roles anywhere in the auth configuration | `DbExplorer/Options/AuthOptions.cs:7-77` — Local/Windows/Google flags and a Google email allow-list only |
| Every Blazor page is already blanket-authorized at the endpoint | `DbExplorer/Program.cs:228-230` — `MapRazorComponents<App>().…RequireAuthorization()` |
| The repo's actual pattern for restricting a capability is a feature-flag Options class | `QueryBuilderOptions.Enabled` (`Options/QueryBuilderOptions.cs:11`) gates the nav link at `MainLayout.razor:53` **and** refuses to render the page at `DiagramPage.razor:19-23`; `AnalyserOptions.Enabled` gates at `MainLayout.razor:74` |
| Options are registered with real validation that executes | `Program.cs:112-115` — `.ValidateDataAnnotations().ValidateOnStart()` |

A policy-based gate needs a source of authority (roles, claims, or a user→permission store). None exists. Building one for a single page is new infrastructure disproportionate to the problem, and it would be the *only* example of that pattern in the codebase — exactly what a context-less implementer should not be asked to invent.

**Verdict: implement `SearchOptions` following `QueryBuilderOptions` exactly** (Phase 7). Same user-visible outcome — an operator can switch off cross-database row scanning, or the whole Search page — with zero new architecture.

**Designed so the bigger approach can be adopted later:** the page performs exactly one gate check at the top of the markup and the nav link exactly one. Adding `[Authorize(Policy = "…")]` later is a one-attribute change at `MetadataSearchPage.razor:12` and does not disturb anything this plan writes.

---

## 4. Hard constraints

Security posture first. Each states the rule, the reason, and the anchor.

**C1 — Never weaken `SqlDialect` identifier validation.**
`ThrowIfInvalidIdentifier` enforces `^[A-Za-z_][A-Za-z0-9_]*$` with a 100 ms regex timeout and per-provider length caps (`DbExplorer/Services/SqlConnectionFactory.cs:296-299`, `:315-334`). Every schema/table/column name that reaches generated SQL — in `ProbeTableAsync` (`DataValueSearchService.cs:220-225`), `BuildLookupSql` (`:274-276`) and `SelectTop` (`MetadataSearchPage.razor:256-257`) — passes through `QuoteIdentifier`/`QuoteQualifiedName` and therefore through this gate. **Do not add an overload that skips validation, do not relax the regex, do not catch and swallow its `ArgumentException`.**

**C2 — Only two categories of string may reach a SQL command, and each through one named sanitizer.**
Pre-complying with **S2077 / S3649**:
- *Identifiers* → `SqlDialect.QuoteIdentifier` / `QuoteQualifiedName` only.
- *User values* → a bound Dapper parameter (`@v`, `@pattern`, `@tableFilter`, `@columnFilter`), **or**, for display-only SQL the app never executes, `SqlTextEscaper.SqlLiteral` (created in Phase 1).
Nothing else is interpolated. Mechanical check in every phase's DoD:
```
grep -rn 'FROM {' DbExplorer/Services/ DbExplorer/Components/ --include=*.cs --include=*.razor
```
Every hit must be an interpolation whose braces contain only a `QuoteIdentifier`/`QuoteQualifiedName` result, a `const int`, or a `SqlTextEscaper.SqlLiteral` result.

**C3 — `ProbeTableAsync` keeps parameterising the search term.**
`DataValueSearchService.cs:233` binds `new { v = likePattern }`. This is the only path in the new code that *executes* user-derived text. It must stay parameterised. `BuildLookupSql` is display-only and separately governed by C2.

**C4 — Audit logging must never record row data.**
`AuditLoggerService`'s contract (`DbExplorer/Services/AuditLoggerService.cs:16-20`) and `AuditEvent`'s (`DbExplorer.Core/Models/Models.cs:27-28`) both promise access metadata only. Phase 6 logs the search *term*, connection names, catalog counts and match counts. **It must not log `DataColumnSample.SampleValue`.** DoD grep: `grep -rn "SampleValue" DbExplorer/Services/` must return only `DataValueSearchService.cs`.

**C5 — Behaviour-preserving extraction first.**
Phase 4 is a pure refactor. `EntityMapLayout.Compute`'s 10 existing tests (`DbExplorer.Tests/Unit/EntityMapLayoutTests.cs`) are the proof for that half; no test may be edited in Phase 4. If a test fails during Phase 4, the extraction is wrong — revert, do not amend the test.

**C6 — Additive changes only to public `DbExplorer.Core` types.**
`AuditAction` (`Models.cs:8-24`) gains one member appended **at the end** of the enum (before the closing brace, after `Logout`) so no existing serialized ordinal shifts. `AuditEvent`, `ForeignKeyInfo`, `DbActionCategory` and every `IServices.cs` interface are unchanged. Any breaking change is a stop-and-ask.

**C7 — Testability split is fixed by this plan; do not renegotiate it.**
Mockability audit:

| Dependency | Shape | Mockable? | Consequence |
|---|---|---|---|
| `IAuditLogger` | interface (`IServices.cs:9-15`) | yes — `Mock.Of<IAuditLogger>()`, precedent `DataControllerTests.cs:21` | Phase 6 asserts audit calls with Moq |
| `ISystemAnalyserStore` | interface | yes | not needed directly |
| `IMetadataService` | interface (`IServices.cs:35+`) | yes — precedent `MetadataControllerTests.cs:28` | not needed; DiagramPage is not unit-tested |
| `DatabaseSelectorState` | **sealed**, ctor takes `IConfiguration` (`SqlConnectionFactory.cs:33,41`) | constructible, not mockable | avoid |
| `DbConnectionFactory` | **sealed**, `new`-ed inline at `DataValueSearchService.cs:123,205` and `MetadataSearchService.cs:115` | **no** | **the search services' I/O paths cannot be unit-tested** |

Therefore: **pure logic goes in `static`/`internal static` helpers with unit tests; I/O stays in the thin service shell and is covered by the runtime acceptance tests in §7.** Do not restructure the services to make them mockable — that is Non-goal 3.

**C8 — Validation attributes must actually execute.**
`SearchOptions` (Phase 7) is registered with `.ValidateDataAnnotations().ValidateOnStart()`, matching `Program.cs:112-115`. Do not add `[Range]`/`[Required]` attributes to any options class that lacks that registration — decorative attributes that never run are worse than none.

**C9 — No regex is added by this plan.**
Pre-complying with **S6444**. Every fix here is achievable with `string` methods, `HashSet`, and LINQ. If you believe you need a regex, you have misread the plan — log it under Deviations and stop.

**C10 — Resource disposal follows the repo's existing idiom.**
`using var` for locals (`DataValueSearchService.cs:115`, `MetadataSearchService.cs:73`); explicit `Cancel()` then `Dispose()` for fields, per `MetadataSearchPage.razor:305-307` and `:313-315`. Use that idiom for the Phase 3 CTS fix — do not introduce `IAsyncDisposable`.

**C11 — Unit tests ship in the same phase and the same commit as the logic they cover.** Never a trailing "add tests" phase. This is what makes the coverage-on-new-code gate condition meaningful.

**C12 — No new CSS selectors** except where a phase explicitly authorises one. Phases 1-6 authorise none. Phase 7 authorises none. Phase 5 *removes* one.

---

# Phase 1 — Escaping module, pure-logic fixes, coverage tooling

Fixes issues **1** and **8**. Delivers goals **C** and **E**. Creates the shared escaping helper that Phase 5 would otherwise have to dedupe.

**Entry state.** `git log -1` is `ca4a794`. Working tree clean. `dotnet test DbExplorer.sln --nologo -v quiet` → `Failed: 0, Passed: 190`.

### Step 1.1 — Add coverage tooling

Edit `DbExplorer.Tests/DbExplorer.Tests.csproj`, appending inside the existing `<ItemGroup>` that ends at line 22 (after the `FluentAssertions` reference):

```xml
    <PackageReference Include="coverlet.collector" Version="6.0.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
```

Verify with:
```
dotnet test DbExplorer.sln --nologo -v quiet --collect:"XPlat Code Coverage"
```
This must produce `DbExplorer.Tests/TestResults/<guid>/coverage.cobertura.xml`. Record the resulting line-rate for `DbExplorer.Core` in the handoff note.

**GOTCHA (coverage output is gitignored — check before assuming failure):** `TestResults/` may already be covered by `.gitignore`. If the file is produced but `git status` stays clean, that is correct and expected — do **not** force-add it. Confirm the file exists on disk with `ls DbExplorer.Tests/TestResults/`.

### Step 1.2 — Create the escaping module (exact code)

New file `DbExplorer/Services/SqlTextEscaper.cs`:

```csharp
namespace DbExplorer.Services;

/// <summary>
/// Escaping helpers for user-supplied text that reaches SQL — either as a bound
/// LIKE pattern or, for display-only SQL the application never executes, as an
/// inline literal.
/// </summary>
internal static class SqlTextEscaper
{
    /// <summary>
    /// Escapes LIKE wildcards in a user's term so they match literally. The result is
    /// bound as a parameter and paired with an explicit ESCAPE clause by the caller.
    /// </summary>
    public static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_")
            .Replace("[", "\\[");

    /// <summary>
    /// Wraps <paramref name="value"/> as a single-quoted SQL literal, escaped for
    /// <paramref name="provider"/>. MySQL treats backslash as an escape character inside
    /// string literals by default (NO_BACKSLASH_ESCAPES off), so doubling single quotes
    /// alone lets a term containing a backslash terminate the literal early.
    /// </summary>
    public static string SqlLiteral(DatabaseProvider provider, string value)
    {
        var escaped = value.Replace("'", "''", StringComparison.Ordinal);
        if (provider == DatabaseProvider.MySql)
            escaped = escaped.Replace("\\", "\\\\", StringComparison.Ordinal);
        return "'" + escaped + "'";
    }
}
```

**Design note.** `SqlLiteral` takes the provider rather than a bool so a fourth provider with different literal rules needs no signature change. `internal` is sufficient — `DbExplorer.csproj:13` declares `InternalsVisibleTo("DbExplorer.Tests")`, so tests reach it directly.

**GOTCHA (escape order in `EscapeLike` is load-bearing):** the backslash replacement **must** come first. If `%` were escaped before `\`, the freshly-inserted `\` would then be doubled and the pattern would match a literal backslash followed by a percent. The order above is byte-identical to the two originals — this is a move, not a rewrite.

### Step 1.3 — Extraction map

| Source (file:line) | Destination | Action |
|---|---|---|
| `DbExplorer/Services/DataValueSearchService.cs:288-292` (`EscapeLike`) | `SqlTextEscaper.EscapeLike` | move verbatim, delete original |
| `DbExplorer/Services/MetadataSearchService.cs:159-163` (`EscapeLike`) | `SqlTextEscaper.EscapeLike` | delete (identical duplicate) |

Update the three call sites — `DataValueSearchService.cs:112`, `:160`, `:162` and `MetadataSearchService.cs:53` — to call `SqlTextEscaper.EscapeLike(...)`. Also delete the now-orphaned XML doc comments at `DataValueSearchService.cs:287` and `MetadataSearchService.cs:158`.

Delete both existing test files, which test the methods being removed:
- `DbExplorer.Tests/Unit/DataValueSearchServiceTests.cs` (18 lines)
- `DbExplorer.Tests/Unit/MetadataSearchServiceTests.cs` (18 lines)

They are replaced by `SqlTextEscaperTests.cs` in Step 1.5, which covers a strict superset of their cases. **Net test count must not drop** — the new file has more `[InlineData]` rows than the two it replaces combined.

### Step 1.4 — Fix issue 1 (exact code)

Replace `DataValueSearchService.cs:271-282` in full:

```csharp
    /// <summary>
    /// Builds a read-only SELECT that finds the matching rows, ready to paste into the Profiler.
    /// The term is embedded as a dialect-escaped literal so the user can run it as-is.
    /// </summary>
    public static string BuildLookupSql(
        DatabaseProvider provider, string schema, string table, string column, string term)
    {
        var dialect = new SqlDialect(provider);
        var qualified = dialect.QuoteQualifiedName(schema, table);
        var col = dialect.QuoteIdentifier(column);
        var literal = SqlTextEscaper.SqlLiteral(provider, "%" + term + "%");
        var likeOp = provider == DatabaseProvider.PostgreSql ? "ILIKE" : "LIKE";
        return provider == DatabaseProvider.SqlServer
            ? $"SELECT TOP 100 * FROM {qualified} WHERE {col} {likeOp} {literal};"
            : $"SELECT * FROM {qualified} WHERE {col} {likeOp} {literal} LIMIT 100;";
    }
```

Note the `%` wildcards moved **inside** `SqlLiteral` so they are added before escaping, not after. That ordering matters: the wildcards are ours, not the user's, and must not themselves be escaped.

### Step 1.5 — Fix issue 8 (exact code)

Replace `DbExplorer.Core/Analysis/AnalyserMath.cs:47-48`:

```csharp
            var offset = (e.Timestamp - start).TotalMinutes / bucketMinutes;
            if (offset < 0) continue;
            var idx = (int)offset;
            if (idx >= bucketCount) continue;
```

**GOTCHA (why the existing test does not catch this):** `Bucketize_EventsOutsideRange_AreExcluded` uses `start.AddMinutes(-5)` — five whole buckets early, so the old `idx < 0` guard fires correctly. The bug lives only in the `(-1, 0)` band, where `(int)` truncates toward zero and produces `0`. `AnalyserPage.Recompute` reaches that band because it fetches `_events` *before* computing `start = DateTimeOffset.UtcNow - window`, so `start` is always fractionally later than the fetch boundary.

### Step 1.6 — Tests (exact code)

New file `DbExplorer.Tests/Unit/SqlTextEscaperTests.cs`:

```csharp
using DbExplorer.Services;
using FluentAssertions;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class SqlTextEscaperTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("PZ12345", "PZ12345")]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("under_score", "under\\_score")]
    [InlineData("[x]", "\\[x]")]
    [InlineData("[bracket]", "\\[bracket]")]
    [InlineData("c\\d", "c\\\\d")]
    [InlineData("back\\slash", "back\\\\slash")]
    public void EscapeLike_EscapesWildcardsLiterally(string input, string expected)
    {
        SqlTextEscaper.EscapeLike(input).Should().Be(expected);
    }

    [Fact]
    public void EscapeLike_EscapesBackslashBeforeWildcards()
    {
        // If '%' were escaped first, the inserted backslash would itself be doubled.
        SqlTextEscaper.EscapeLike("%\\%").Should().Be("\\%\\\\\\%");
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer, "plain", "'plain'")]
    [InlineData(DatabaseProvider.PostgreSql, "plain", "'plain'")]
    [InlineData(DatabaseProvider.MySql, "plain", "'plain'")]
    public void SqlLiteral_PlainValue_IsQuoted(DatabaseProvider provider, string input, string expected)
    {
        SqlTextEscaper.SqlLiteral(provider, input).Should().Be(expected);
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    [InlineData(DatabaseProvider.MySql)]
    public void SqlLiteral_SingleQuote_IsDoubled(DatabaseProvider provider)
    {
        SqlTextEscaper.SqlLiteral(provider, "O'Reilly").Should().Be("'O''Reilly'");
    }

    [Fact]
    public void SqlLiteral_MySql_DoublesBackslash()
    {
        SqlTextEscaper.SqlLiteral(DatabaseProvider.MySql, @"AB\CD").Should().Be(@"'AB\\CD'");
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public void SqlLiteral_NonMySql_LeavesBackslashAlone(DatabaseProvider provider)
    {
        SqlTextEscaper.SqlLiteral(provider, @"AB\CD").Should().Be(@"'AB\CD'");
    }

    [Fact]
    public void SqlLiteral_MySql_TermCannotTerminateTheLiteralEarly()
    {
        var literal = SqlTextEscaper.SqlLiteral(DatabaseProvider.MySql, @"x\' OR 1=1 -- ");

        // The backslash is doubled, so the following quote is a doubled-quote escape,
        // not an escaped quote that would close the string.
        literal.Should().Be(@"'x\\'' OR 1=1 -- '");
    }
}
```

New file `DbExplorer.Tests/Unit/BuildLookupSqlTests.cs`:

```csharp
using DbExplorer.Services;
using FluentAssertions;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class BuildLookupSqlTests
{
    [Fact]
    public void SqlServer_UsesTopAndBracketQuoting()
    {
        var sql = DataValueSearchService.BuildLookupSql(
            DatabaseProvider.SqlServer, "dbo", "Orders", "Reference", "PZ12345");

        sql.Should().Be("SELECT TOP 100 * FROM [dbo].[Orders] WHERE [Reference] LIKE '%PZ12345%';");
    }

    [Fact]
    public void PostgreSql_UsesIlikeAndLimit()
    {
        var sql = DataValueSearchService.BuildLookupSql(
            DatabaseProvider.PostgreSql, "public", "orders", "reference", "PZ12345");

        sql.Should().Be("SELECT * FROM \"public\".\"orders\" WHERE \"reference\" ILIKE '%PZ12345%' LIMIT 100;");
    }

    [Fact]
    public void MySql_UsesBacktickQuotingAndLimit()
    {
        var sql = DataValueSearchService.BuildLookupSql(
            DatabaseProvider.MySql, "shop", "orders", "reference", "PZ12345");

        sql.Should().Be("SELECT * FROM `shop`.`orders` WHERE `reference` LIKE '%PZ12345%' LIMIT 100;");
    }

    [Fact]
    public void SingleQuoteInTerm_IsDoubled()
    {
        var sql = DataValueSearchService.BuildLookupSql(
            DatabaseProvider.SqlServer, "dbo", "Customers", "Name", "O'Reilly");

        sql.Should().Contain("'%O''Reilly%'");
    }

    [Fact]
    public void MySql_BackslashInTerm_IsEscaped()
    {
        var sql = DataValueSearchService.BuildLookupSql(
            DatabaseProvider.MySql, "shop", "orders", "reference", @"AB\CD");

        sql.Should().Be(@"SELECT * FROM `shop`.`orders` WHERE `reference` LIKE '%AB\\CD%' LIMIT 100;");
    }

    [Fact]
    public void WildcardsInTermAreNotEscaped_TheLiteralIsDisplayOnly()
    {
        // BuildLookupSql produces SQL the user runs by hand; a '%' they typed stays a wildcard.
        var sql = DataValueSearchService.BuildLookupSql(
            DatabaseProvider.SqlServer, "dbo", "Orders", "Reference", "50%");

        sql.Should().Contain("'%50%%'");
    }

    [Theory]
    [InlineData("Order Items")]
    [InlineData("Orders; DROP TABLE x")]
    [InlineData("1Orders")]
    [InlineData("Orders--")]
    public void InvalidTableIdentifier_Throws(string table)
    {
        var act = () => DataValueSearchService.BuildLookupSql(
            DatabaseProvider.SqlServer, "dbo", table, "Reference", "x");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("bad schema")]
    [InlineData("dbo;--")]
    public void InvalidSchemaIdentifier_Throws(string schema)
    {
        var act = () => DataValueSearchService.BuildLookupSql(
            DatabaseProvider.SqlServer, schema, "Orders", "Reference", "x");

        act.Should().Throw<ArgumentException>();
    }
}
```

Append to `DbExplorer.Tests/Unit/AnalyserMathTests.cs`, inside the class before the closing brace:

```csharp
    [Fact]
    public void Bucketize_EventJustBeforeWindowStart_IsNotCountedInFirstBucket()
    {
        var start = DateTimeOffset.UtcNow;
        var events = new List<DbActionEvent> { Evt(5, ts: start.AddSeconds(-30)) };

        var buckets = AnalyserMath.Bucketize(events, start, bucketMinutes: 1, bucketCount: 10);

        buckets.Sum(b => b.Total).Should().Be(0);
    }

    [Fact]
    public void Bucketize_EventExactlyAtWindowStart_LandsInFirstBucket()
    {
        var start = DateTimeOffset.UtcNow;
        var events = new List<DbActionEvent> { Evt(5, ts: start) };

        var buckets = AnalyserMath.Bucketize(events, start, bucketMinutes: 1, bucketCount: 10);

        buckets[0].Total.Should().Be(1);
    }

    [Fact]
    public void Bucketize_EventAtLastBucketBoundary_IsExcluded()
    {
        var start = DateTimeOffset.UtcNow;
        var events = new List<DbActionEvent> { Evt(5, ts: start.AddMinutes(10)) };

        var buckets = AnalyserMath.Bucketize(events, start, bucketMinutes: 1, bucketCount: 10);

        buckets.Sum(b => b.Total).Should().Be(0);
    }
```

### Definition of done — Phase 1

1. `dotnet test DbExplorer.sln --nologo -v quiet` → `Failed: 0`, passed **≥ 205** (190 − 10 deleted `[InlineData]` cases + ~25 new).
2. `dotnet test DbExplorer.sln --collect:"XPlat Code Coverage"` produces a `coverage.cobertura.xml`; its path is recorded in the handoff note.
3. `grep -rn "EscapeLike" DbExplorer/Services/` returns hits **only** in `SqlTextEscaper.cs` (definition) plus the 4 call sites — **zero** method definitions in `DataValueSearchService.cs` or `MetadataSearchService.cs`.
4. `grep -rn "Replace(\"'\", \"''\")" DbExplorer/` returns hits only in `SqlTextEscaper.cs`.
5. C2 grep passes: every `FROM {` interpolation contains only quoted identifiers, `const int`, or a `SqlLiteral` result.
6. `git diff --stat` touches only: `DbExplorer.Tests.csproj`, `SqlTextEscaper.cs` (new), `DataValueSearchService.cs`, `MetadataSearchService.cs`, `AnalyserMath.cs`, `SqlTextEscaperTests.cs` (new), `BuildLookupSqlTests.cs` (new), `AnalyserMathTests.cs`, and the two deleted test files.

**Abort/rollback.** `git revert <commit range>`. Fully reversible — no schema, config or deployed-state change.

### Handoff note template — Phase 1

```
## Phase 1 — Escaping module, pure-logic fixes, coverage tooling — DONE {{YYYY-MM-DD}}
Commits: {{hash1}} (coverage tooling), {{hash2}} (SqlTextEscaper + issue 1), {{hash3}} (issue 8 + tests)
Test result: Failed: 0, Passed: {{n}} (baseline was 190)
Coverage report: {{path}} — DbExplorer.Core line-rate {{x}}%
DoD greps: EscapeLike definitions outside SqlTextEscaper = {{0}}; FROM{ interpolations audited = {{n}}, all compliant
Issues closed: 1, 8. Hotspot H2 code change landed (review decision recorded in Phase 7).
Notes: {{anything surprising}}
```

---

# Phase 2 — Search service reliability

Fixes issues **2**, **9**, **11**.

**Entry state.** Phase 1 done and green. `grep -c "EscapeLike" DbExplorer/Services/SqlTextEscaper.cs` → ≥ 1.

### Step 2.1 — Fix issue 2: isolate per-catalog failures (exact code)

Replace `DataValueSearchService.cs:95-102` (the `foreach (var catalog in catalogs)` block):

```csharp
            var candidates = new List<CandidateTable>();
            var catalogsFailed = 0;
            foreach (var catalog in catalogs)
            {
                try
                {
                    var cols = await GetCandidateColumnsAsync(option, baseConnectionString, catalog, request, ct);
                    candidates.AddRange(cols
                        .GroupBy(c => (c.SchemaName, c.TableName))
                        .Select(g => new CandidateTable(catalog, g.Key.SchemaName, g.Key.TableName,
                            g.Select(c => c.ColumnName).ToList())));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    catalogsFailed++;
                    _logger.LogDebug(ex,
                        "Data search skipped catalog {Catalog} on {Connection}", catalog, option.Name);
                }
            }
```

This mirrors `MetadataSearchService.cs:82-85` exactly — same `when (ex is not OperationCanceledException)` filter, same `LogDebug` level, same message shape.

**GOTCHA (`when (ex is not OperationCanceledException)` is not optional):** a plain `catch (Exception)` would swallow the caller's cancellation. `SearchConnectionAsync` deliberately rethrows `OperationCanceledException` at `:143-145` so `MetadataSearchPage.RunDataSearchAsync` can distinguish a user cancel (`:299`) from a connection failure. Swallowing it here makes the 180-second timeout at `MetadataSearchPage.razor:292` silently produce empty results instead of a cancel.

`catalogsFailed` is used by the Phase 6 audit event. Until then the compiler will warn it is assigned but unused — that is expected and acceptable within Phase 2; it becomes live in Phase 6. If your build treats warnings as errors (it does not today — Phase 0 build produced 10 warnings, 0 errors), log it under Deviations rather than deleting the variable.

### Step 2.2 — Fix issue 9: no match without renderable samples

At `DataValueSearchService.cs:254`, insert immediately before the `return`:

```csharp
            // The SQL LIKE matched under the column's collation, but the ordinal re-check
            // found nothing to show. Reporting a match the UI cannot render makes the
            // headline count disagree with the visible rows.
            if (samples.Count == 0) return null;

            return new DataTableMatch(catalog, schema, table, samples, rows.Count);
```

### Step 2.3 — Fix issue 11: cap before accumulating (exact code)

Replace `MetadataSearchService.cs:72-95`:

```csharp
            var hits = new ConcurrentBag<CrossDbColumnHit>();
            var found = 0;
            using var gate = new SemaphoreSlim(CatalogConcurrency);
            var scans = catalogs.Select(async catalog =>
            {
                // Cheap pre-check: once the per-connection cap is met, remaining catalogs
                // are pointless work — the Take() below would discard them anyway.
                if (Volatile.Read(ref found) >= MaxHitsPerConnection) return;

                await gate.WaitAsync(ct);
                try
                {
                    if (Volatile.Read(ref found) >= MaxHitsPerConnection) return;

                    foreach (var hit in await SearchCatalogAsync(option, baseConnectionString, catalog, pattern, ct))
                    {
                        hits.Add(hit);
                        Interlocked.Increment(ref found);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Name search skipped catalog {Catalog} on {Connection}", catalog, option.Name);
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(scans);

            var ordered = hits
                .OrderBy(h => h.CatalogName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.SchemaName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.TableName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxHitsPerConnection)
                .ToList();
            return new ConnectionSearchResult(option.Name, option.Provider, ordered, null);
```

**Design note.** The cap is a best-effort throttle, not a hard limit: catalogs already in flight when the threshold is crossed still finish. That is deliberate — a hard limit would need a shared lock on the hot path for no user-visible benefit. The `.Take(MaxHitsPerConnection)` at the end remains the authoritative cap, so the UI's "first 500 shown" message at `MetadataSearchPage.razor:65-66` stays correct.

**GOTCHA (the early return must be inside the gate too):** the pre-check before `WaitAsync` avoids queueing, but a task already queued when the cap is crossed must re-check after acquiring. Both checks are required; deleting either defeats the fix or reintroduces the 30,000-row accumulation.

**GOTCHA (`Volatile.Read` not a plain read):** `found` is written with `Interlocked.Increment` from up to `CatalogConcurrency` threads. A plain read is not guaranteed to observe those writes. Use `Volatile.Read` as written — do **not** simplify to `if (found >= …)`.

### Step 2.4 — Tests

`ProbeTableAsync` and `SearchConnectionAsync` need a live database (C7) — they get runtime acceptance in §7, not unit tests. No new unit tests in this phase. **This is the one phase exempt from C11**, because the changed code is pure I/O orchestration with no extractable pure logic. Do not invent a mock-heavy test to satisfy the rule; do not restructure the services to enable one (Non-goal 3).

### Definition of done — Phase 2

1. `dotnet test DbExplorer.sln --nologo -v quiet` → `Failed: 0`, passed unchanged from Phase 1.
2. `grep -n "catch (Exception ex) when (ex is not OperationCanceledException)" DbExplorer/Services/DataValueSearchService.cs` → exactly 1 hit.
3. `grep -n "Volatile.Read(ref found)" DbExplorer/Services/MetadataSearchService.cs` → exactly 2 hits.
4. `grep -n "if (samples.Count == 0) return null;" DbExplorer/Services/DataValueSearchService.cs` → exactly 1 hit.
5. `git diff --stat` touches only `DataValueSearchService.cs` and `MetadataSearchService.cs`.

**Abort/rollback.** `git revert <commit range>`. Fully reversible.

### Handoff note template — Phase 2

```
## Phase 2 — Search service reliability — DONE {{YYYY-MM-DD}}
Commits: {{hash1}} (issue 2 + 9, DataValueSearchService), {{hash2}} (issue 11, MetadataSearchService)
Test result: Failed: 0, Passed: {{n}}
DoD greps: all 4 pass ({{paste counts}})
Issues closed: 2, 9, 11.
Not runtime-verified in this phase: per-catalog failure isolation and the hit cap need a
multi-database server — deferred to the §7 acceptance run.
Notes: {{anything surprising}}
```

---

# Phase 3 — DiagramPage reliability and disposal

Fixes issues **3**, **4**, **7**, **10**. All in one file.

**Entry state.** Phase 2 done and green.

### Step 3.1 — Fix issue 4: dispose the debounce CTS (exact code)

Replace `DiagramPage.razor:307-308`:

```csharp
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = _searchCts = new CancellationTokenSource();
```

This is the idiom already used at `MetadataSearchPage.razor:305-307` (C10).

**GOTCHA (dispose the old one, then create — never reorder):** `_searchCts` is both the field being replaced and the source of the token captured by the in-flight `DebouncedRebuildAsync`. Cancelling before disposing is what lets the waiting `Task.Delay` observe cancellation and return at `:315` instead of throwing `ObjectDisposedException`.

### Step 3.2 — Fix issue 3: guard the column load (exact code)

Add the logger injection. Insert after `DiagramPage.razor:12` (`@inject NavigationManager Nav`):

```razor
@inject ILogger<DiagramPage> Logger
```

Precedent: `DataGrid.razor:7`, `ExplorerPage.razor:9`, `Home.razor:12`.

Replace `DiagramPage.razor:440-454` in full:

```csharp
    private async Task ToggleNodeAsync(EntityMapNode node)
    {
        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded && node.Columns is null)
        {
            node.IsLoadingColumns = true;
            node.Refresh();
            StateHasChanged();

            try
            {
                node.Columns = (await Metadata.GetColumnsAsync(node.SchemaName, node.TableName)).ToList();
            }
            catch (Exception ex)
            {
                // An empty list renders the node's "No columns" state and, critically, is
                // non-null — so collapsing and re-expanding does not retry forever.
                Logger.LogWarning(ex, "Could not load columns for {Table}", node.QualifiedName);
                node.Columns = [];
            }
            finally
            {
                node.IsLoadingColumns = false;
            }
        }
        node.Refresh();
        StateHasChanged();
    }
```

**GOTCHA (setting `Columns = []` is the fix, not just clearing the flag):** `EntityMapNodeWidget.razor:28` renders "No columns" for `Columns is { Count: 0 }` and `:32` renders the list for non-null. Leaving `Columns` null after a failure would clear the spinner but re-enter the load branch on every expand, hammering a database that is already failing.

### Step 3.3 — Fix issue 10: surface DB-switch failures (exact code)

Replace `DiagramPage.razor:254-268`:

```csharp
    private void HandleDatabaseChanged()
    {
        var gen = Interlocked.Increment(ref _dbGeneration);
        _ = InvokeAsync(async () =>
        {
            _searchTerm = "";
            _focusRootId = null;
            try
            {
                await LoadCatalogAsync();
            }
            catch (Exception ex)
            {
                // Without this the task faults silently and the page keeps rendering the
                // PREVIOUS database's tables, with no indication anything went wrong.
                Logger.LogWarning(ex, "Could not load catalog after database change");
                _allObjects = new();
                _allFks = new();
                _schemaNames = new();
                _selectedSchemas.Clear();
            }
            if (gen != _dbGeneration) return;

            InitDiagram();
            RebuildMap();
            StateHasChanged();
        });
    }
```

Clearing `_allObjects` makes the existing empty state at `:46-51` render — "No tables found in this catalog. Check that the selected database and your permissions are correct." — which is already the correct message for this situation. No new UI is needed.

### Step 3.4 — Fix issue 7: one link per relationship (exact code)

Replace `DiagramPage.razor:418-431`:

```csharp
        if (!_linksSuppressed)
        {
            // A composite FK yields one ForeignKeyInfo per column (the catalog query joins
            // sys.foreign_key_columns / key_column_usage), so draw at most one arrow per
            // ordered table pair — otherwise N identical links stack on the same two ports.
            var drawn = new HashSet<(string Child, string Parent)>();
            foreach (var fk in _effectiveFks)
            {
                var childId = Qualify(fk.SchemaName, fk.TableName);
                var parentId = Qualify(fk.ReferencedSchema, fk.ReferencedTable);
                if (!drawn.Add((childId.ToUpperInvariant(), parentId.ToUpperInvariant()))) continue;
                if (!_nodes.TryGetValue(childId, out var child)) continue;
                if (!_nodes.TryGetValue(parentId, out var parent)) continue;

                var link = new LinkModel(new SinglePortAnchor(child.RightPort), new SinglePortAnchor(parent.LeftPort))
                {
                    TargetMarker = LinkMarker.Arrow
                };
                _blazorDiagram.Links.Add(link);
            }
        }
```

And replace `DiagramPage.razor:385-386` so the suppression threshold counts relationships, not columns:

```csharp
        _effectiveEdgeCount = _effectiveFks
            .Select(fk => (Qualify(fk.SchemaName, fk.TableName).ToUpperInvariant(),
                           Qualify(fk.ReferencedSchema, fk.ReferencedTable).ToUpperInvariant()))
            .Distinct()
            .Count();
        _linksSuppressed = _effectiveEdgeCount > MaxLinks;
```

**GOTCHA (`_effectiveFks.Count` still drives the FK list panel, and should):** the panel at `:131-157` lists one row per FK *column* and its header at `:136` says `@_effectiveFks.Count relationship(s)`. Change that header text to `@_effectiveFks.Count foreign key column(s)` — the list genuinely is per-column and the constraint name column at `:148-151` makes composite keys legible. Do **not** deduplicate the list itself; it is the documented fallback source of truth ("fallback source of truth if a link fails to draw", `:130`).

**GOTCHA (`ToUpperInvariant` for the set key, not a case-insensitive comparer):** `HashSet<(string, string)>` cannot take a `StringComparer` for tuple elements. Normalising with `ToUpperInvariant()` matches the existing precedent at `EntityMapLayout.cs:27`, which does exactly this for the same reason.

### Step 3.5 — Tests

`DiagramPage` is a Razor component with no bUnit dependency in the test project (`DbExplorer.Tests.csproj:17-22`) and adding one is out of scope. Verification is the §7 runtime acceptance flow. No unit tests this phase — C11 does not apply because no pure logic is added.

### Definition of done — Phase 3

1. `dotnet test DbExplorer.sln --nologo -v quiet` → `Failed: 0`, passed unchanged.
2. `grep -n "_searchCts?.Dispose();" DbExplorer/Components/Pages/DiagramPage.razor` → **2** hits (the new one at ~:308 and the existing one in `Dispose` at ~:218).
3. `grep -c "@inject ILogger<DiagramPage> Logger" DbExplorer/Components/Pages/DiagramPage.razor` → 1.
4. `grep -n "IsLoadingColumns = false" DbExplorer/Components/Pages/DiagramPage.razor` → exactly 1 hit, and it is inside a `finally`.
5. `grep -n "relationship(s)" DbExplorer/Components/Pages/DiagramPage.razor` → 0 hits (replaced by "foreign key column(s)").
6. `git diff --stat` touches only `DiagramPage.razor`.

**Abort/rollback.** `git revert <commit range>`. Fully reversible.

### Handoff note template — Phase 3

```
## Phase 3 — DiagramPage reliability and disposal — DONE {{YYYY-MM-DD}}
Commits: {{hash1}} (issues 4 + 10), {{hash2}} (issue 3), {{hash3}} (issue 7)
Test result: Failed: 0, Passed: {{n}}
DoD greps: all 6 pass ({{paste counts}})
Issues closed: 3, 4, 7, 10.
Not runtime-verified: composite-FK dedup needs a DB with a multi-column FK — §7 acceptance.
Notes: {{anything surprising}}
```

---

# Phase 4 — Complexity reduction (S3776 ×2 + quadratic scan)

Fixes issues **5**, **6**, **12**. **Pure refactor — no behaviour change** (C5).

**Entry state.** Phase 3 done and green. `DbExplorer.Tests/Unit/EntityMapLayoutTests.cs` untouched since `ca4a794` (`git log --oneline -- DbExplorer.Tests/Unit/EntityMapLayoutTests.cs` shows only `ca4a794`).

### Step 4.1 — Split `EntityMapLayout.Compute` (issue 5)

Current score **21**. Extract along the three section comments already in the file (`:33`, `:63`, `:81`), which mark the natural seams.

| Source (file:line) | Destination | Action |
|---|---|---|
| `EntityMapLayout.cs:33-61` (BFS layer assignment) | `private static Dictionary<string, int> AssignLayers(nodes, outNb, inNb)` | move verbatim |
| `EntityMapLayout.cs:63-79` (barycenter sweep) | `private static Dictionary<int, List<string>> OrderWithinLayers(layer, outNb, inNb)` | move verbatim |
| `EntityMapLayout.cs:81-95` (coordinates) | `private static Dictionary<string, (double X, double Y)> AssignCoordinates(layers, byId)` | move verbatim |

`Compute` then reads as: build `byId`/`outNb`/`inNb` (`:22-31`, stays), call the three helpers, return `new LayoutResult(...)`. Target score for `Compute` ≤ 5; each helper ≤ 10.

**GOTCHA (the local function `Median` moves with the barycenter block and keeps its nesting cost):** `Median` is declared inside the `for` at `:71` and contributes +3 for the ternary at `:76` (nesting 2: `for` body = 1, nested function = 2). Inside `OrderWithinLayers` the `for` is at nesting 0, so `Median`'s body sits at nesting 1 and the ternary costs +2. That drop is a side-effect of the extraction, not a behaviour change — do not "optimise" `Median` further.

**GOTCHA (`layers` must stay a `Dictionary<int, List<string>>`, not a `List<List<string>>`):** `:67` and `:84` both loop with `for (var k = …; layers.ContainsKey(k); k++)`. BFS guarantees contiguous layer numbers from 0, so a list would work — but changing the type is a behaviour-adjacent edit outside this phase's remit. Move verbatim.

### Step 4.2 — Split `RebuildMap` and fix the quadratic scan (issues 6, 12)

Current score **21**. Extract:

| Source (file:line, post-Phase-3) | Destination | Action |
|---|---|---|
| `DiagramPage.razor:344-363` (visible-set selection) | `private List<DatabaseObjectInfo> SelectVisibleTables()` | move verbatim |
| `DiagramPage.razor:394-416` (node construction) | `private void BuildNodes(List<DatabaseObjectInfo> tables, List<LayoutEdge> edges, LayoutResult layout)` | move, then apply Step 4.3 |
| `DiagramPage.razor:418-431` (link construction, as rewritten in Phase 3) | `private void BuildLinks()` | move verbatim |

### Step 4.3 — Precompute the lookups (issue 12, exact code)

Inside `BuildNodes`, replace the four per-node scans (`:403-404` and `:406-412`) with lookups built once:

```csharp
    private void BuildNodes(List<DatabaseObjectInfo> tables, List<LayoutEdge> edges, LayoutResult layout)
    {
        // Built once per rebuild. The previous form ran four full scans per node, which is
        // quadratic in (nodes x edges) and runs on every debounced keystroke.
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var outDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges)
        {
            outDegree[e.SourceId] = outDegree.GetValueOrDefault(e.SourceId) + 1;
            inDegree[e.TargetId] = inDegree.GetValueOrDefault(e.TargetId) + 1;
        }

        var fkColumnsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in _effectiveFks)
        {
            AddFkColumn(fkColumnsByTable, Qualify(fk.SchemaName, fk.TableName), fk.ColumnName);
            AddFkColumn(fkColumnsByTable, Qualify(fk.ReferencedSchema, fk.ReferencedTable), fk.ReferencedColumn);
        }

        foreach (var table in tables)
        {
            var id = Qualify(table.SchemaName, table.ObjectName);
            var (x, y) = layout.Positions.TryGetValue(id, out var p) ? p : (0, 0);

            var node = new EntityMapNode(new Point(x, y))
            {
                SchemaName = table.SchemaName,
                TableName = table.ObjectName,
                InDegree = inDegree.GetValueOrDefault(id),
                OutDegree = outDegree.GetValueOrDefault(id),
            };
            node.FkColumns = fkColumnsByTable.TryGetValue(id, out var cols)
                ? cols
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _blazorDiagram!.Nodes.Add(node);
            _nodes[id] = node;
        }
    }

    private static void AddFkColumn(Dictionary<string, HashSet<string>> map, string tableId, string column)
    {
        if (!map.TryGetValue(tableId, out var set))
            map[tableId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(column);
    }
```

**GOTCHA (the degree dictionaries must be case-insensitive AND keyed on the same `Qualify` output):** the originals compared with `string.Equals(..., StringComparison.OrdinalIgnoreCase)` against `id`. `StringComparer.OrdinalIgnoreCase` on the dictionary reproduces that exactly. A default (case-sensitive) dictionary would silently zero the FK badge at `EntityMapNodeWidget.razor:11-16` for any database whose catalog casing differs from the FK metadata casing — which is the normal case on SQL Server with a case-insensitive collation.

**GOTCHA (`fkColumnsByTable` hands out the *same* `HashSet` instance it stores):** that is safe here because `EntityMapNode.FkColumns` is only ever read (`EntityMapNodeWidget.razor:42`) and each rebuild constructs a fresh map. Do not add a defensive copy — it would allocate one set per node for no benefit.

Target: `RebuildMap` ≤ 8, `BuildNodes` ≤ 5, `BuildLinks` ≤ 6, `SelectVisibleTables` ≤ 5.

### Definition of done — Phase 4

1. `dotnet test DbExplorer.sln --nologo -v quiet` → `Failed: 0`, passed unchanged from Phase 3. **No test file modified** — `git diff --stat` must show zero changes under `DbExplorer.Tests/`.
2. `grep -n "edges.Count(" DbExplorer/Components/Pages/DiagramPage.razor` → 0 hits.
3. `grep -n "_effectiveFks$" -A2 DbExplorer/Components/Pages/DiagramPage.razor | grep -c "\.Where("` → 0 inside `BuildNodes`.
4. Manual complexity recount recorded in the handoff note for all five extracted methods, each ≤ 15.
5. `git diff --stat` touches only `EntityMapLayout.cs` and `DiagramPage.razor`.

**Abort/rollback.** `git revert <commit range>`. Fully reversible. If the `EntityMapLayoutTests` suite fails at any point, the extraction is wrong — revert and retry; **do not edit the test** (C5).

### Handoff note template — Phase 4

```
## Phase 4 — Complexity reduction — DONE {{YYYY-MM-DD}}
Commits: {{hash1}} (EntityMapLayout.Compute split), {{hash2}} (RebuildMap split + lookup precompute)
Test result: Failed: 0, Passed: {{n}} — EntityMapLayoutTests all green, file unmodified
Recounted cognitive complexity:
  Compute {{n}} | AssignLayers {{n}} | OrderWithinLayers {{n}} | AssignCoordinates {{n}}
  RebuildMap {{n}} | SelectVisibleTables {{n}} | BuildNodes {{n}} | BuildLinks {{n}}
Issues closed: 5, 6, 12.
Notes: {{anything surprising}}
```

---

# Phase 5 — Maintainability cleanup

Fixes issues **13**, **14**, **15**.

**Entry state.** Phase 4 done and green.

### Step 5.1 — Shared clipboard helper (issues 13, 14) (exact code)

New file `DbExplorer/Services/ClipboardService.cs`:

```csharp
using Microsoft.JSInterop;

namespace DbExplorer.Services;

/// <summary>
/// Wraps the browser clipboard so pages do not each need their own JS-interop
/// error handling. Copy failures are non-fatal by design: the user still has the
/// text on screen and can select it manually.
/// </summary>
public sealed class ClipboardService
{
    private readonly IJSRuntime _js;
    private readonly ILogger<ClipboardService> _logger;

    public ClipboardService(IJSRuntime js, ILogger<ClipboardService> logger)
    {
        _js = js;
        _logger = logger;
    }

    /// <summary>Returns true when the browser confirmed the copy.</summary>
    public async Task<bool> TryCopyAsync(string text)
    {
        try
        {
            return await _js.InvokeAsync<bool>("copyToClipboard", text);
        }
        catch (JSDisconnectedException)
        {
            // Circuit torn down mid-copy (user navigated away). Not worth a log line.
            return false;
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Clipboard copy failed");
            return false;
        }
    }
}
```

Register in `DbExplorer/Program.cs`, immediately after line 157 (`builder.Services.AddScoped<QueryHandoffState>();`):

```csharp
builder.Services.AddScoped<ClipboardService>();
```

**GOTCHA (`JSDisconnectedException` derives from `JSException` — order the catches correctly):** the specific catch must come first or it is unreachable and the compiler will error. The order above is correct.

**GOTCHA (scoped, not singleton):** `IJSRuntime` is scoped per circuit in Blazor Server. Registering `ClipboardService` as a singleton would capture one circuit's `IJSRuntime` and throw for every other user. `AddScoped` matches the neighbouring registrations at `Program.cs:94-99` and `:155-157`.

Then rewire both call sites:

| File | Change |
|---|---|
| `DbExplorer/Components/Pages/MetadataSearchPage.razor:11` | replace `@inject IJSRuntime JS` with `@inject ClipboardService Clipboard` |
| `DbExplorer/Components/Pages/MetadataSearchPage.razor:272-276` | replace the method with `private Task CopySqlAsync(string sql) => Clipboard.TryCopyAsync(sql);` |
| `DbExplorer/Components/Pages/MetadataSearchPage.razor:4` | delete `@using Microsoft.JSInterop` if no other usage remains |
| `DbExplorer/Components/Pages/AnalyserPage.razor` | same three changes (`@inject IJSRuntime JS` → `@inject ClipboardService Clipboard`; `CopySqlAsync` body; `@using Microsoft.JSInterop`) |

**GOTCHA (verify `JS` has no other use on either page before deleting the injection):** run `grep -n "JS\." <page>` first. On `MetadataSearchPage.razor` the only use is `:274`. On `AnalyserPage.razor` confirm before deleting — if `JS` is used elsewhere, keep both injections.

### Step 5.2 — Remove the dead `Dimmed` state (issue 15)

Search and focus both *remove* non-matching tables from the visible set (`DiagramPage.razor:350-363`) rather than dimming them, so the dim state was superseded before it shipped. Delete it.

| File:line | Action |
|---|---|
| `DbExplorer/Components/Diagram/EntityMapNode.cs:40-41` | delete the `Dimmed` property and its doc comment |
| `DbExplorer/Components/Diagram/EntityMapNodeWidget.razor:5` | delete `@(Node.Dimmed ? "emap-node--dim" : "")` from the class expression |
| `DbExplorer/wwwroot/css/app.css:1407` | delete the `.emap-node--dim { opacity: 0.35; }` rule |

This is the one CSS change authorised by this plan (C12) — a removal.

**GOTCHA (leave `Highlighted` alone):** `Highlighted` at `EntityMapNode.cs:38` *is* assigned, at `DiagramPage.razor:434-435`, and `.emap-node--highlight` at `app.css:1406` is live. Only `Dimmed` is dead.

### Step 5.3 — Test

Append to `DbExplorer.Tests/Unit/` a new file `QueryHandoffStateTests.cs` — `QueryHandoffState` shipped in `ca4a794` with no tests and is pure state (C7: trivially testable, no I/O):

```csharp
using DbExplorer.Services;
using FluentAssertions;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class QueryHandoffStateTests
{
    [Fact]
    public void Consume_WithoutSet_ReturnsNull()
    {
        new QueryHandoffState().Consume().Should().BeNull();
    }

    [Fact]
    public void Consume_ReturnsPendingSqlOnceThenClears()
    {
        var state = new QueryHandoffState();
        state.Set("SELECT 1;");

        state.Consume().Should().Be("SELECT 1;");
        state.Consume().Should().BeNull();
        state.PendingSql.Should().BeNull();
    }

    [Fact]
    public void Set_OverwritesAnUnconsumedQuery()
    {
        var state = new QueryHandoffState();
        state.Set("SELECT 1;");
        state.Set("SELECT 2;");

        state.Consume().Should().Be("SELECT 2;");
    }
}
```

### Definition of done — Phase 5

1. `dotnet test DbExplorer.sln --nologo -v quiet` → `Failed: 0`, passed **≥ Phase 4 + 3**.
2. `grep -rn "catch { " DbExplorer/Components/Pages/` → 0 hits.
3. `grep -rn "Dimmed\|emap-node--dim" DbExplorer/` → 0 hits.
4. `grep -rn "InvokeAsync<bool>(\"copyToClipboard\"" DbExplorer/` → exactly 1 hit, in `ClipboardService.cs`.
5. `grep -c "AddScoped<ClipboardService>" DbExplorer/Program.cs` → 1.
6. No new CSS selector: `git diff DbExplorer/wwwroot/css/app.css` shows deletions only.

**Abort/rollback.** `git revert <commit range>`. Fully reversible.

### Handoff note template — Phase 5

```
## Phase 5 — Maintainability cleanup — DONE {{YYYY-MM-DD}}
Commits: {{hash1}} (ClipboardService + both call sites), {{hash2}} (Dimmed removal + QueryHandoffState tests)
Test result: Failed: 0, Passed: {{n}}
DoD greps: all 6 pass ({{paste counts}})
Issues closed: 13, 14, 15. ALL 15 ISSUES NOW CLOSED.
Notes: {{anything surprising}}
```

---

# Phase 6 — Hotspot H1: audit logging for search

Delivers goal **B**.

**Entry state.** Phase 5 done and green. All 15 issue rows in the Progress table read `done`.

### Step 6.1 — Add the audit action (exact code)

In `DbExplorer.Core/Models/Models.cs`, append **after** `Logout,` at line 23 and before the closing brace at `:24` (C6 — append only):

```csharp
    /// <summary>User ran a cross-connection search for table/column names or row values.</summary>
    Search,
```

In `DbExplorer/Services/AuditLoggerService.cs`, add to the EventId block after line 36:

```csharp
    private static readonly EventId SearchEvent          = new(1008, "Search");
```

Add to the `eventId` switch (after `AuditAction.Logout` at `:67`):

```csharp
                AuditAction.Search         => SearchEvent,
```

Add to the `Category` switch (after `AuditAction.Logout` at `:97`):

```csharp
                    AuditAction.Search         => DbActionCategory.Metadata,
```

**Design note.** `DbActionCategory` gains no member. Its `Metadata` doc comment at `Models.cs:233` already reads "Schema/metadata reads (schemas, objects, columns, FKs, **search**)" — the category was designed to cover this. Reusing it keeps the change additive on one enum instead of two and needs no change to the Analyser page's category breakdown.

**GOTCHA (both switches have `_ =>` defaults, so a missed arm compiles and silently degrades):** omit the `eventId` arm and search events log under `EventId 1000`; omit the `Category` arm and they land in `DbActionCategory.Other`, polluting the Analyser dashboard. Neither fails the build. Add both, and verify with the DoD greps.

### Step 6.2 — Plumb the username (exact code)

The services are scoped and have no access to `AuthenticationStateProvider`. The page resolves it and passes it in — matching `ProfilerPage.razor:449-454`, which caches the username in `OnInitializedAsync` precisely to avoid re-resolving per operation.

`DbExplorer/Services/DataValueSearchService.cs:6-10` — add a field to the request record (additive, defaulted, so the existing construction site still compiles):

```csharp
public sealed record DataValueSearchRequest(
    string Value,
    IReadOnlyList<string> ConnectionNames,
    string? TablePattern,
    string? ColumnPattern,
    string Username = "anonymous");
```

`DbExplorer/Services/MetadataSearchService.cs:48` — add a defaulted parameter:

```csharp
    public async Task<IReadOnlyList<ConnectionSearchResult>> SearchAsync(
        string term, string username = "anonymous", CancellationToken ct = default)
```

**GOTCHA (adding a defaulted parameter before `ct` is a source-compatible but call-site-silent change):** the single existing caller is `MetadataSearchPage.razor:283`, which passes `_cts!.Token` **positionally**. After the signature change that token would bind to `username`. You must update that call to `SearchService.SearchAsync(term, _username, _cts!.Token)`. Verify with the DoD grep — the compiler will **not** catch this, because `CancellationToken` is not implicitly convertible to `string`… but the reverse ordering *does* fail to compile, so in practice you get a build error. Do not "fix" it by reordering parameters; fix the call site.

### Step 6.3 — Inject the logger and emit the events

Add `IAuditLogger audit` to both constructors (`DataValueSearchService.cs:56-64`, `MetadataSearchService.cs:38-46`) as a new final parameter with a `_audit` field. `IAuditLogger` is a singleton (`Program.cs:161`) injected into scoped services — permitted and already done by `AuditLoggerService`'s own dependency on the singleton `ISystemAnalyserStore`.

In `MetadataSearchService.SearchAsync`, after `var results = await Task.WhenAll(tasks);`:

```csharp
        _audit.Log(new AuditEvent(
            DateTimeOffset.UtcNow, username, AuditAction.Search,
            SchemaName: null, ObjectName: null,
            RowCount: results.Sum(r => r.Hits.Count), ElapsedMs: sw.ElapsedMilliseconds,
            Sql: null,
            Context: new Dictionary<string, string?>
            {
                ["mode"] = "names",
                ["term"] = term,
                ["connections"] = string.Join(",", results.Select(r => r.ConnectionName)),
                ["failed"] = results.Count(r => r.Error is not null).ToString(),
            }));
```

Wrap the method body in a `var sw = System.Diagnostics.Stopwatch.StartNew();` at the top.

In `DataValueSearchService.SearchAsync`, the equivalent after `var results = await Task.WhenAll(tasks);`:

```csharp
        _audit.Log(new AuditEvent(
            DateTimeOffset.UtcNow, request.Username, AuditAction.Search,
            SchemaName: null, ObjectName: null,
            RowCount: results.Sum(r => r.Matches.Count), ElapsedMs: sw.ElapsedMilliseconds,
            Sql: null,
            Context: new Dictionary<string, string?>
            {
                ["mode"] = "data",
                ["term"] = request.Value,
                ["tableFilter"] = request.TablePattern,
                ["columnFilter"] = request.ColumnPattern,
                ["connections"] = string.Join(",", results.Select(r => r.ConnectionName)),
                ["tablesScanned"] = results.Sum(r => r.TablesScanned).ToString(),
            }));
```

**GOTCHA (C4 — never log sample values):** `RowCount` here is the number of matched *tables*, not rows, and no `DataColumnSample` is referenced. The `Sql: null` is deliberate: `BuildLookupSql` output is generated per-row in the UI, not by the service, and logging it would embed the search term twice. Do not add it.

**GOTCHA (`_audit.Log` must be outside the per-connection try/catch):** one event per user-initiated search, not one per connection. Emitting inside `SearchConnectionAsync` would produce N events for one user action and make the audit trail useless for answering "who searched for what".

### Step 6.4 — Page wiring

In `MetadataSearchPage.razor`:
- add `@using Microsoft.AspNetCore.Components.Authorization` and `@inject AuthenticationStateProvider AuthState` (precedent `ProfilerPage.razor:14`);
- add `private string _username = "anonymous";`
- convert `OnInitialized` (`:234-238`) to `OnInitializedAsync`, keeping the existing body and adding the username resolution verbatim from `ProfilerPage.razor:452-453`:

```csharp
    protected override async Task OnInitializedAsync()
    {
        // Default: scan the currently selected connection only — the safe, cheap starting point.
        _selectedConnections.Add(SelectorState.Current.Name);

        // Cache username once so we don't call GetAuthenticationStateAsync() per search.
        var authState = await AuthState.GetAuthenticationStateAsync();
        _username = authState.User.Identity?.Name ?? "anonymous";
    }
```

- update `:283` to `SearchService.SearchAsync(term, _username, _cts!.Token)`;
- update `:295-296` to `new DataValueSearchRequest(term, _selectedConnections.ToList(), _tablePattern, _columnPattern, _username)`.

### Step 6.5 — Tests (exact code)

New file `DbExplorer.Tests/Unit/SearchAuditTests.cs`. These cover the *audit event shape*, which is pure logic; the services' I/O is not exercised because no connection is configured, so `SearchAsync` returns early or every connection errors — either way the audit event is still emitted, which is exactly what we assert.

```csharp
using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DbExplorer.Tests.Unit;

public class SearchAuditTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static DatabaseSelectorState Selector() => new(EmptyConfig());

    [Fact]
    public async Task MetadataSearch_BlankTerm_DoesNotAudit()
    {
        var audit = new Mock<IAuditLogger>();
        var svc = new MetadataSearchService(
            Selector(), EmptyConfig(), NullLogger<MetadataSearchService>.Instance, audit.Object);

        await svc.SearchAsync("   ", "alice");

        audit.Verify(a => a.Log(It.IsAny<AuditEvent>()), Times.Never);
    }

    [Fact]
    public async Task MetadataSearch_RecordsSearchActionWithUsernameAndTerm()
    {
        var audit = new Mock<IAuditLogger>();
        AuditEvent? captured = null;
        audit.Setup(a => a.Log(It.IsAny<AuditEvent>())).Callback<AuditEvent>(e => captured = e);

        var svc = new MetadataSearchService(
            Selector(), EmptyConfig(), NullLogger<MetadataSearchService>.Instance, audit.Object);

        await svc.SearchAsync("orders", "alice");

        captured.Should().NotBeNull();
        captured!.Action.Should().Be(AuditAction.Search);
        captured.Username.Should().Be("alice");
        captured.Sql.Should().BeNull();
        captured.Context.Should().ContainKey("mode").WhoseValue.Should().Be("names");
        captured.Context.Should().ContainKey("term").WhoseValue.Should().Be("orders");
    }

    [Fact]
    public async Task DataValueSearch_BlankValue_DoesNotAudit()
    {
        var audit = new Mock<IAuditLogger>();
        var svc = new DataValueSearchService(
            Selector(), EmptyConfig(), NullLogger<DataValueSearchService>.Instance, audit.Object);

        await svc.SearchAsync(new DataValueSearchRequest("", [], null, null, "alice"));

        audit.Verify(a => a.Log(It.IsAny<AuditEvent>()), Times.Never);
    }

    [Fact]
    public async Task DataValueSearch_RecordsSearchActionAndNeverLogsSampleValues()
    {
        var audit = new Mock<IAuditLogger>();
        AuditEvent? captured = null;
        audit.Setup(a => a.Log(It.IsAny<AuditEvent>())).Callback<AuditEvent>(e => captured = e);

        var svc = new DataValueSearchService(
            Selector(), EmptyConfig(), NullLogger<DataValueSearchService>.Instance, audit.Object);

        await svc.SearchAsync(new DataValueSearchRequest("PZ12345", [], "ord", "ref", "bob"));

        captured.Should().NotBeNull();
        captured!.Action.Should().Be(AuditAction.Search);
        captured.Username.Should().Be("bob");
        captured.Sql.Should().BeNull();
        captured.Context.Should().ContainKey("mode").WhoseValue.Should().Be("data");
        captured.Context.Should().ContainKey("tableFilter").WhoseValue.Should().Be("ord");
    }

    [Fact]
    public void AuditAction_Search_IsAppendedLast_SoExistingOrdinalsAreStable()
    {
        // C6: appended at the end, so persisted/logged ordinals for earlier members do not shift.
        ((int)AuditAction.MetadataAccess).Should().Be(0);
        ((int)AuditAction.Logout).Should().Be(6);
        ((int)AuditAction.Search).Should().Be(7);
    }
}
```

**GOTCHA (`DatabaseSelectorState` with empty config yields 3 default options, not zero):** its constructor falls back to three built-in options at `SqlConnectionFactory.cs:45-53` when nothing is configured. So `MetadataSearchService.SearchAsync` will attempt three connections, each failing to resolve a connection string and returning an error result — which is fine, the audit event still fires. `DataValueSearchService` filters by `request.ConnectionNames`, so passing `[]` means zero connections and zero I/O. Both are intentional in the tests above.

### Definition of done — Phase 6

1. `dotnet test DbExplorer.sln --nologo -v quiet` → `Failed: 0`, passed **≥ Phase 5 + 5**.
2. `grep -c "AuditAction.Search" DbExplorer/Services/AuditLoggerService.cs` → **2** (one per switch).
3. `grep -n "Search," DbExplorer.Core/Models/Models.cs` → the member is the **last** before the enum's closing brace.
4. C4 grep: `grep -rn "SampleValue" DbExplorer/Services/` → hits only in `DataValueSearchService.cs`.
5. `grep -n "_audit.Log" DbExplorer/Services/` → exactly 2 hits, both in a `SearchAsync` method, neither inside a `SearchConnectionAsync`.
6. `grep -n "SearchService.SearchAsync(term, _username" DbExplorer/Components/Pages/MetadataSearchPage.razor` → 1 hit.

**Abort/rollback.** `git revert <commit range>`. Reversible in code. **Note:** once deployed, audit records already written under `EventId 1008` remain in the log sink — reverting stops new ones but does not retract old ones. That is desirable and needs no action.

### Handoff note template — Phase 6

```
## Phase 6 — Hotspot H1: audit logging for search — DONE {{YYYY-MM-DD}}
Commits: {{hash1}} (AuditAction.Search + AuditLoggerService arms), {{hash2}} (service wiring + username plumbing), {{hash3}} (page wiring + tests)
Test result: Failed: 0, Passed: {{n}}
DoD greps: all 6 pass ({{paste counts}})
Hotspot H1 status: FIXED — one audit event per user-initiated search, no row data logged.
Runtime check done: {{yes/no}} — searched for "{{term}}" with Audit:Enabled=true and confirmed
  a "AUDIT Search | user=… | context={mode=…, term=…}" line in logs/dbexplorer-*.log
Notes: {{anything surprising}}
```

---

# Phase 7 — Hotspot H3: search gating, UX, and final verification

Delivers goal **D** and closes the plan.

**Entry state.** Phase 6 done and green. All 15 issue rows `done`; H1 and H2 recorded.

### Step 7.1 — `SearchOptions` (exact code)

New file `DbExplorer/Options/SearchOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace DbExplorer.Options;

/// <summary>
/// Feature flags for the cross-connection Search page.
///
/// Name search reads only catalog metadata. Data-value search reads actual row
/// contents from every accessible database on the selected connections, so it is
/// flagged separately — operators can leave name search on and turn row scanning off.
/// </summary>
public sealed class SearchOptions
{
    /// <summary>
    /// When false the /search route is disabled and the nav link is hidden.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When false the "By data value" mode is hidden and its service is never invoked.
    /// Name search remains available.
    /// </summary>
    public bool DataValueSearchEnabled { get; init; } = true;

    /// <summary>
    /// Maximum connections a single data-value scan may target. Guards against one user
    /// launching a full row scan across every configured server at once.
    /// </summary>
    [Range(1, 50)]
    public int MaxConnectionsPerDataScan { get; init; } = 3;
}
```

Register in `DbExplorer/Program.cs` immediately after the `AnalyserOptions` block ending at line 115, following that block's exact shape (C8 — validation that actually runs):

```csharp
builder.Services.AddOptions<SearchOptions>()
    .Bind(builder.Configuration.GetSection("Search"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

Add to `DbExplorer/appsettings.json` after the `"QueryBuilder"` block (which ends at line 44):

```json
  "Search": {
    "Enabled": true,
    "DataValueSearchEnabled": true,
    "MaxConnectionsPerDataScan": 3
  },
```

**GOTCHA (`[Range]` on `MaxConnectionsPerDataScan` only executes because of `.ValidateDataAnnotations()`):** `McpOptions` and `MetadataOptions` at `Program.cs:116-119` are bound *without* it, so any attributes on them would be decorative. Copy the `AnalyserOptions` registration at `:112-115`, not the `McpOptions` one at `:116-117`.

### Step 7.2 — Gate the page and the nav link

`DbExplorer/Components/Pages/MetadataSearchPage.razor` — add `@using DbExplorer.Options`, `@using Microsoft.Extensions.Options`, `@inject IOptions<SearchOptions> SearchOpts`, then insert immediately after the `<PageTitle>` at `:16`, exactly mirroring `DiagramPage.razor:19-23`:

```razor
@if (!SearchOpts.Value.Enabled)
{
    <div class="profiler-error">Search is disabled. Set <code>Search:Enabled = true</code> in appsettings.json.</div>
    return;
}
```

`DbExplorer/Components/Layout/MainLayout.razor` — add `@inject IOptions<SearchOptions> SearchOpts` after line 10, and wrap the Search NavLink at `:46-52` in `@if (SearchOpts.Value.Enabled) { … }`, matching the `QBOpts` wrapper at `:53-73` and the `AnalyserOpts` wrapper at `:74`.

### Step 7.3 — Gate the data-value mode

In `MetadataSearchPage.razor`:
- the mode toggle at `:25` — wrap the "By data value" button in `@if (SearchOpts.Value.DataValueSearchEnabled) { … }`;
- in `SetMode`, refuse the transition defensively: `if (mode == Mode.Data && !SearchOpts.Value.DataValueSearchEnabled) return;`
- in `RunDataSearchAsync` (`:288-301`), add as the first line: `if (!SearchOpts.Value.DataValueSearchEnabled) return;`
- enforce the connection cap in `RunDataSearchAsync` before building the request:

```csharp
        if (_selectedConnections.Count > SearchOpts.Value.MaxConnectionsPerDataScan)
        {
            _dataError = $"Select at most {SearchOpts.Value.MaxConnectionsPerDataScan} connection(s) for a data scan. " +
                         $"You have {_selectedConnections.Count} selected.";
            return;
        }
        _dataError = null;
```

with `private string? _dataError;` added to the field block at `:219-232`.

**GOTCHA (three enforcement points, not one):** hiding the button is presentation; `SetMode` guards direct state manipulation; `RunDataSearchAsync` guards the actual invocation. A Blazor Server circuit is server-side so the button check is genuinely load-bearing, but the service-invocation guard is what makes the flag a real control rather than a cosmetic one.

### Step 7.4 — UX specification

**(a) Exact user-facing copy — verbatim.**

| Location | Copy |
|---|---|
| Page disabled banner | `Search is disabled. Set Search:Enabled = true in appsettings.json.` (with `Search:Enabled = true` inside `<code>`) |
| Names-mode intro (unchanged, `:31`) | `Find tables and columns by name across every connection and every accessible database on each.` |
| Data-mode intro (replaces `:113-116`) | `Find which tables contain a value in their text columns, across the connections you select and every accessible database on each. This scans row data, so narrow the surface with the optional table and column name filters — at most @DataValueSearchService.MaxCandidateTables tables are scanned per connection, returning up to @DataValueSearchService.SampleRows sample rows per match. Every search is recorded in the audit log.` |
| Too many connections | `Select at most {N} connection(s) for a data scan. You have {M} selected.` |
| Connections label hint (new, beside the label at `:132`) | `Up to @SearchOpts.Value.MaxConnectionsPerDataScan at a time.` |
| Names empty state (replaces `No matches.` at `:77`) | `No tables or columns match that name here. Try a shorter fragment — the search matches anywhere in the name.` |
| Data empty state (replaces `No table data matched.` at `:175`) | `No rows in this connection's text columns contain that value. Try removing the table or column filters, or check you selected the right connection.` |
| Connection unavailable (unchanged, service-supplied) | `This connection could not be searched — it may be unreachable or misconfigured.` |
| Truncation notice (unchanged, `:66`) | `first {N} matches shown — narrow the search` |

The audit sentence is appended to the data-mode intro because telling users their row scans are logged is both honest and a mild deterrent — it is the user-facing half of hotspot H1.

**(b) Component mapping — inventory only, no new CSS (C12).**

| UI element | Existing class | Exemplar |
|---|---|---|
| Disabled banner | `.profiler-error` | `app.css:1104`; used at `DiagramPage.razor:21` |
| Intro / notice block | `.profiler-readonly-notice` | `app.css:1124`; used at `MetadataSearchPage.razor:30` |
| Empty state | `.empty-state` | `app.css:677`; used at `DiagramPage.razor:87` |
| Hint text | `.qb-hint` | `app.css:1186`; used at `MetadataSearchPage.razor:42` |
| Loading spinner | `.spinner` | `app.css:667`; used at `MetadataSearchPage.razor:48` |
| Provider / type badge | `.type-badge` | `app.css:546`; used at `MetadataSearchPage.razor:61` |
| Primary / ghost / small button | `.profiler-btn`, `.profiler-btn--ghost`, `.profiler-btn--sm` | `app.css:1037`; used at `MetadataSearchPage.razor:95-98` |
| Result panel | `.profiler-panel`, `.profiler-panel--open` | used at `MetadataSearchPage.razor:59` |
| Toolbar row | `.emap-toolbar-row` | used at `MetadataSearchPage.razor:23` |

**(c) State design.**

| State | Rendering |
|---|---|
| First run / no search yet | `_nameResults` and `_dataResults` both null → no result region at all (current behaviour, correct) |
| Loading | `.qb-hint` + `.spinner` — `Searching all connections…` / `Scanning row data — this can take a moment…` (unchanged) |
| Partial failure | per-connection panel renders `.profiler-error` with the service message; other connections still render results |
| Empty success | `.qb-hint` inside the panel body with the new copy from (a) |
| Feature disabled | `.profiler-error` banner, page body suppressed |
| Cap exceeded | `.profiler-error` above the form, form stays interactive |

Status → badge mapping for a connection panel's meta line (`:62-68`, `:162-166`):

| Status | Meta text | Class |
|---|---|---|
| Error | `unavailable` | `.profiler-panel-meta` |
| Truncated | `first {N} matches shown — narrow the search` | `.profiler-panel-meta` |
| Normal | `{N} match(es)` / `{N} table(s) matched · {M} scanned` | `.profiler-panel-meta` |

**(d) Interaction basics.** Already correct in the shipped page and must be preserved: both search boxes sit inside `<form @onsubmit=…>` (`:34`, `:119`) so **Enter submits**; submit buttons carry `disabled="@(_searching || …)"` (`:37`, `:127`) so they are **disabled while busy**; every input has an `aria-label` (`:36`, `:122`, `:124`, `:126`). Verify all three still hold after the edits.

**(e) Dynamic copy.** The caps in the intro come from `DataValueSearchService.MaxCandidateTables` and `.SampleRows` (already dynamic at `:115-116`); the connection cap comes from `SearchOpts.Value.MaxConnectionsPerDataScan`. **No cap is hardcoded in copy.**

**(f) UX acceptance checklist** — see §7 Verification, Phase 7 block.

### Step 7.5 — Record the hotspot review decisions

Append to `continue.md` under a new `## Hotspot review record` heading (this is the artifact that lets someone mark the hotspots resolved in SonarQube):

```
### H1 — Insufficient logging (CWE-778 / OWASP A09:2021)
Status: FIXED (Phase 6). One AuditEvent per user-initiated search, AuditAction.Search,
no row data. Reviewer answer: search was an unintended gap in the audit trail, now closed.

### H2 — SQL built by string concatenation (CWE-89 / OWASP A03:2021)
Status: SAFE. BuildLookupSql produces display-only SQL the application never executes;
the user runs it by hand in the Profiler, which already accepts arbitrary typed SQL, so
there is no privilege boundary crossed. Identifiers pass SqlDialect.QuoteQualifiedName
(regex-validated, SqlConnectionFactory.cs:296-334); the term is dialect-escaped by
SqlTextEscaper.SqlLiteral (Phase 1). The executing path, ProbeTableAsync, parameterises
the term as @v and always did.

### H3 — Broad data access (CWE-200 / OWASP A01:2021)
Status: FIXED (Phase 7), with a documented deviation from the reviewer's suggestion.
Suggested: an authorization policy. Implemented: SearchOptions feature flags, because the
repo has zero policy/role precedent (Program.cs:78 is a bare AddAuthorization(); AuthOptions
has no roles) and feature-flag Options is the established pattern for capability gating
(QueryBuilderOptions, AnalyserOptions). Operators can now disable the page entirely, disable
row-value scanning while keeping name search, and cap connections per scan. A policy can be
layered later by adding one [Authorize(Policy=…)] attribute.
```

### Definition of done — Phase 7

1. `dotnet test DbExplorer.sln --nologo -v quiet` → `Failed: 0`.
2. App starts clean with the new options: `dotnet run --project DbExplorer` reaches "Now listening on" with no `OptionsValidationException`.
3. Setting `"MaxConnectionsPerDataScan": 0` in `appsettings.json` **fails startup** with an `OptionsValidationException` — proves `[Range]` executes (C8). Revert to 3 afterwards.
4. `grep -c "SearchOpts.Value.Enabled" DbExplorer/Components/Layout/MainLayout.razor DbExplorer/Components/Pages/MetadataSearchPage.razor` → 1 each.
5. `grep -c "DataValueSearchEnabled" DbExplorer/Components/Pages/MetadataSearchPage.razor` → **3** (markup, `SetMode`, `RunDataSearchAsync`).
6. Copy greps: `grep -c "Every search is recorded in the audit log." DbExplorer/Components/Pages/MetadataSearchPage.razor` → 1; `grep -c "No tables or columns match that name here" …` → 1; `grep -c "No rows in this connection's text columns contain that value" …` → 1.
7. **No new CSS selectors:** `git diff DbExplorer/wwwroot/css/app.css` is empty for this phase.
8. Every Progress row in `continue.md` reads `done`; zero blocking open questions; hotspot review record appended; closing summary appended listing what could not be runtime-verified.

**Abort/rollback.** `git revert <commit range>` reverts the code. **`appsettings.json` is not reverted by a code rollback in a deployed environment** — if the `"Search"` section has been copied to a production config, removing it is safe (all three properties have defaults that reproduce today's behaviour: `Enabled = true`, `DataValueSearchEnabled = true`, `MaxConnectionsPerDataScan = 3`). Note that the connection cap of 3 is a **behaviour change for existing users** who currently may select unlimited connections — this is intentional and is the substance of the H3 fix, but it means Phase 7 is *not* purely additive from a user's perspective. Call it out in the closing summary.

### Handoff note template — Phase 7

```
## Phase 7 — Hotspot H3: gating, UX, final verification — DONE {{YYYY-MM-DD}}
Commits: {{hash1}} (SearchOptions + registration + appsettings), {{hash2}} (page + nav gating), {{hash3}} (UX copy + empty states)
Test result: Failed: 0, Passed: {{n}}
Startup check: clean at MaxConnectionsPerDataScan=3; OptionsValidationException at 0 (confirmed [Range] runs)
DoD greps: all 8 pass ({{paste counts}})
UX acceptance checklist: {{n}}/{{n}} passed — see §7
Hotspot review record: appended to continue.md
PLAN COMPLETE. Closing summary appended.
```

---

## 5. Execution order & commit plan

Every commit boundary is also a `continue.md` checkpoint boundary. Build + `dotnet test` between every commit.

| Phase | Commits | Boundaries |
|---|---|---|
| 1 | 3 | (a) coverage tooling alone — verify a report is produced before touching code; (b) `SqlTextEscaper` + both call sites + issue 1 + `SqlTextEscaperTests` + `BuildLookupSqlTests` + delete the two old test files; (c) issue 8 + `AnalyserMathTests` additions |
| 2 | 2 | (a) `DataValueSearchService` — issues 2 and 9 together, same file; (b) `MetadataSearchService` — issue 11 |
| 3 | 3 | (a) issues 4 and 10 — both are lifecycle/CTS, same mental model; (b) issue 3; (c) issue 7 (largest single change, keep it isolated so a revert is surgical) |
| 4 | 2 | (a) `EntityMapLayout.Compute` split — tests must stay green with zero test edits; (b) `RebuildMap` split + lookup precompute |
| 5 | 2 | (a) `ClipboardService` + both call sites; (b) `Dimmed` removal + `QueryHandoffStateTests` |
| 6 | 3 | (a) `AuditAction.Search` + both `AuditLoggerService` switch arms — smallest possible core change, verify build; (b) service constructors + username plumbing + audit emission; (c) page wiring + `SearchAuditTests` |
| 7 | 3 | (a) `SearchOptions` + `Program.cs` + `appsettings.json` — verify startup validation before any UI change; (b) page and nav gating; (c) UX copy, empty states, connection cap |

**Total: 18 commits across 7 phases.**

---

## 6. Interfaces designed for later phases

Called out explicitly so no later phase needs a signature change:

- **`SqlTextEscaper.SqlLiteral(DatabaseProvider, string)`** (Phase 1) takes the provider, not a bool, so a fourth provider with different literal rules is a switch arm rather than a signature change.
- **`DataValueSearchRequest.Username`** (Phase 6) is added as a *defaulted trailing* record parameter, so the Phase 1-5 construction site at `MetadataSearchPage.razor:295` keeps compiling until Phase 6 updates it deliberately.
- **`MetadataSearchService.SearchAsync(term, username, ct)`** places `username` before `ct` because `CancellationToken` is conventionally last throughout this codebase (`IServices.cs:35-56` — every method ends with `CancellationToken ct = default`).
- **`SearchOptions.MaxConnectionsPerDataScan`** exists from the start even though only the page enforces it, so moving enforcement into `DataValueSearchService` later needs no new option.
- **`ClipboardService.TryCopyAsync` returns `bool`** rather than `void`/`Task`, so a later phase can show a "Copied" toast without changing the signature. Phases 5-7 ignore the result.

### Degradation contract

`ClipboardService.TryCopyAsync` returns `false` — never throws — when the browser clipboard is unavailable, the circuit is gone, or the JS function is missing. Callers must treat `false` as "not copied" and take no corrective action: the SQL is already visible on screen for manual selection. **Do not** surface an error for a failed copy.

---

## 7. Verification

### Test artifacts to create

**Artifact A — SQL Server scratch database.** Needed for Phases 2, 3, 6, 7 acceptance.

```sql
CREATE DATABASE DbExplorerSonarTest;
GO
USE DbExplorerSonarTest;
GO
CREATE TABLE dbo.Customers (
    CustomerId INT NOT NULL PRIMARY KEY,
    Name NVARCHAR(100) NOT NULL,
    Reference NVARCHAR(50) NULL);
CREATE TABLE dbo.Orders (
    OrderTenantId INT NOT NULL,
    OrderId INT NOT NULL,
    CustomerId INT NOT NULL,
    Notes NVARCHAR(400) NULL,
    CONSTRAINT PK_Orders PRIMARY KEY (OrderTenantId, OrderId),
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers(CustomerId));
-- Composite FK: the issue-7 regression case. Two columns, one relationship, one arrow.
CREATE TABLE dbo.OrderLines (
    OrderTenantId INT NOT NULL,
    OrderId INT NOT NULL,
    LineNo INT NOT NULL,
    CONSTRAINT PK_OrderLines PRIMARY KEY (OrderTenantId, OrderId, LineNo),
    CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (OrderTenantId, OrderId)
        REFERENCES dbo.Orders(OrderTenantId, OrderId));
CREATE VIEW dbo.CustomerOrders AS
    SELECT c.Name, o.OrderId FROM dbo.Customers c JOIN dbo.Orders o ON o.CustomerId = c.CustomerId;
GO
INSERT INTO dbo.Customers VALUES
    (1, 'Acme Ltd',      'PZ12345'),
    (2, 'O''Reilly Ltd', 'PZ99999'),
    (3, 'Back\Slash Co', 'AB\CD'),      -- issue 1 / H2 regression case
    (4, 'Percent 50%',   '50%');        -- EscapeLike regression case
INSERT INTO dbo.Orders VALUES (1, 100, 1, 'contains PZ12345 in notes');
GO
-- Issue 2 regression case: a database the app can see but not open.
CREATE DATABASE DbExplorerSonarDenied;
GO
```

To make `DbExplorerSonarDenied` inaccessible to the app's login without dropping it, `DENY CONNECT` for that principal. If your environment cannot do this, record it as not-verified.

**Artifact B — a slow query for cancellation testing.** Run in the Profiler against the scratch DB:
```sql
WAITFOR DELAY '00:00:20'; SELECT 1;
```

### Per-phase acceptance

**Phase 1.** Command: `dotnet test DbExplorer.sln --nologo -v quiet --collect:"XPlat Code Coverage"`. Pass: `Failed: 0`, and `DbExplorer.Tests/TestResults/*/coverage.cobertura.xml` exists and is non-empty. Then confirm the issue-1 fix by inspection: `BuildLookupSqlTests.MySql_BackslashInTerm_IsEscaped` passes — it fails against the pre-Phase-1 code, which is the proof the bug was real.

**Phase 2.** Requires Artifact A including `DbExplorerSonarDenied`.
- *Issue 2:* select the scratch connection, Search → By data value, term `PZ12345`, no filters. **Pass:** results appear for `DbExplorerSonarTest` despite `DbExplorerSonarDenied` being unreadable. **Fail:** the connection panel shows `This connection could not be searched`.
- *Issue 9:* term `PZ12345`. **Pass:** every table row rendered has at least one sample value; the headline `N table(s) contain …` equals the number of distinct tables in the table body.
- *Issue 11:* term `e` (matches almost everything). **Pass:** results return and the panel meta reads `first 500 matches shown — narrow the search`. Not directly observable — verify by log inspection or accept as covered by code review; record which.

**Phase 3.** Requires Artifact A.
- *Issue 3:* Entity Map → expand a table, then revoke SELECT on it and expand another. **Pass:** the failing node shows "No columns", not a permanent spinner, and `logs/dbexplorer-*.log` contains `Could not load columns for`. If permissions cannot be manipulated, stop the database mid-expand instead.
- *Issue 4:* type 10 characters into the Entity Map search box. **Pass:** no `ObjectDisposedException` in the log; map settles on the final term.
- *Issue 7:* Entity Map, `dbo` schema. **Pass:** **exactly one** arrow between `OrderLines` and `Orders` despite the two-column FK. The FK panel below still lists **two** rows (one per column) with header `2 foreign key column(s)` — that is correct and intended.
- *Issue 10:* switch the connection selector to a connection with a bad connection string. **Pass:** the map clears to the "No tables found in this catalog" empty state and a warning appears in the log — it does **not** keep showing the previous database's tables.

**Phase 4.** Command: `dotnet test DbExplorer.sln --nologo -v quiet`. Pass: `Failed: 0` **and** `git diff --stat HEAD~2 -- DbExplorer.Tests/` is empty. Then re-run the Phase 3 Entity Map flows above; every observation must be identical (this is a pure refactor).

**Phase 5.** Click "Copy SQL" on the Search page and on an Analyser detail row. **Pass:** clipboard contains the SQL; no unhandled exception. Then, with the browser's clipboard permission denied, click again. **Pass:** nothing visibly happens, no error toast, no exception in the log above Debug level.

**Phase 6.** Set `"Audit": { "Enabled": true, "LogSql": true }`. Run one name search for `Customer` and one data search for `PZ12345`. **Pass:** `logs/dbexplorer-*.log` contains exactly **two** lines matching `AUDIT Search`, the first with `context={mode=names…}` and the second with `context={mode=data…}`, both carrying the real username. **Fail:** more than two lines (per-connection emission), or any line containing `Acme Ltd` or `PZ12345` as a *sample value* rather than as the search term.

**Phase 7 — UX acceptance checklist.** Every row must pass.

| # | Check | Pass criterion |
|---|---|---|
| 1 | `Search:Enabled = false` | Nav link gone; navigating to `/search` shows the disabled banner, not a blank page or an error |
| 2 | `Search:DataValueSearchEnabled = false` | "By data value" button absent; name search still works |
| 3 | `MaxConnectionsPerDataScan = 0` | App **fails to start** with `OptionsValidationException` |
| 4 | Select 4 connections with cap 3 | Error message names both numbers; form stays usable; no service call made |
| 5 | Name search with no matches | New empty-state copy appears, and it tells the user what to try next |
| 6 | Data search with no matches | New empty-state copy appears, and it names the filters as the thing to relax |
| 7 | Enter key in each of the 4 inputs | Submits the form |
| 8 | During a search | Submit button is disabled; spinner visible |
| 9 | Tab through the page | Every button and input is reachable; no focus trap |
| 10 | Dark theme | All new copy legible against `.profiler-error` / `.empty-state` / `.qb-hint` backgrounds |
| 11 | Light theme | Same |
| 12 | Data-mode intro | States the audit-logging fact and both caps, with caps sourced from config not hardcoded |

### Environments that cannot be runtime-tested

State these in the closing summary as **not runtime-verified**:

- **PostgreSQL and MySQL paths.** Every provider-specific branch changed by this plan — `SqlTextEscaper.SqlLiteral`'s MySQL arm (the *primary* fix for issue 1), the `ILIKE`/`LIMIT` forms in `BuildLookupSql`, and the per-provider SQL in both search services — is verified by **unit test only** unless a live PostgreSQL and MySQL instance is available. The MySQL backslash fix is the highest-value change in the plan and the least likely to be runtime-verified; say so explicitly.
- **Accent-insensitive collations** (issue 9's trigger condition). The scratch database uses the server default. Unless you can create a table with an `_AI` collation and a row containing `café`, issue 9's fix is verified by code inspection only.
- **The 30,000-hit accumulation path** (issue 11) needs a server with dozens of databases and a broad term. Unlikely to be reproducible locally.
