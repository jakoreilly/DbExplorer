# continue.md — session handoff state for plan.md
# RULES FOR EVERY SESSION (read before doing anything else):
# 1. Read plan.md fully, then this file. This file outranks your memory
#    and any summary you were given. plan.md outranks this file only for
#    constraints and design — never for progress state.
# 2. Verify before trusting: run the build/test command below and the
#    current phase's Entry state checks. If reality disagrees with this
#    file, STOP — record the discrepancy under Deviations and reconcile
#    with evidence before writing any code.
# 3. Update this file at EVERY commit boundary, not just phase ends.
#    A killed session must cost at most one commit of progress.
# 4. Never re-plan, never re-litigate scope, never "improve" earlier
#    phases. If plan.md seems wrong, log it and stop for human review.
# 5. Keep "Next action" a single imperative sentence a stranger could
#    execute. Not "continue Phase 3" — instead "Create IFooProvider per
#    plan.md Phase 3 step 2, starting with the exact-code block."

## Ground truth
- Build/test command: `dotnet test DbExplorer.sln --nologo -v quiet`
- Coverage command (Phase 1 onward): `dotnet test DbExplorer.sln --nologo -v quiet --collect:"XPlat Code Coverage"`
- Last verified green: `ca4a794` — 2026-08-08 — `Failed: 0, Passed: 190, Skipped: 0, Total: 190, Duration: 9 s`
- Build also verified green at `ca4a794` on 2026-08-08: `Build succeeded. 10 Warning(s), 0 Error(s)`
- Working tree at last update: clean (only `plan.md` and `continue.md` are new and uncommitted)

## Progress
| Phase | Status | Commits | Verified (DoD check + result) |
|-------|--------|---------|-------------------------------|
| 1 — Escaping module, pure-logic fixes, coverage tooling | not started | — | — |
| 2 — Search service reliability | not started | — | — |
| 3 — DiagramPage reliability and disposal | not started | — | — |
| 4 — Complexity reduction (S3776 ×2 + quadratic scan) | not started | — | — |
| 5 — Maintainability cleanup | not started | — | — |
| 6 — Hotspot H1: audit logging for search | not started | — | — |
| 7 — Hotspot H3: gating, UX, final verification | not started | — | — |

Issue tracker (15 total, from the Sonar review of `ca4a794`):

| Issue | Phase | Status |
|---|---|---|
| 1 — MySQL literal escaping in `BuildLookupSql` | 1 | open |
| 8 — `Bucketize` negative-fraction truncation | 1 | open |
| 2 — catalog failure aborts connection scan | 2 | open |
| 9 — match returned with zero renderable samples | 2 | open |
| 11 — hit cap applied after accumulation | 2 | open |
| 3 — `IsLoadingColumns` stuck on failure | 3 | open |
| 4 — S2930 undisposed `CancellationTokenSource` | 3 | open |
| 7 — composite FK draws duplicate links | 3 | open |
| 10 — discarded faulting Task on DB switch | 3 | open |
| 5 — S3776 `EntityMapLayout.Compute` (21) | 4 | open |
| 6 — S3776 `RebuildMap` (21) | 4 | open |
| 12 — quadratic scan in `RebuildMap` | 4 | open |
| 13 — S2486 empty catch (MetadataSearchPage) | 5 | open |
| 14 — S2486 empty catch (AnalyserPage) | 5 | open |
| 15 — dead `Dimmed` property | 5 | open |

Hotspots:

| Hotspot | Phase | Status |
|---|---|---|
| H1 — no audit logging on search | 6 | To Review |
| H2 — SQL built by string concatenation | 1 (code) / 7 (record) | To Review |
| H3 — broad cross-database data access | 7 | To Review |

## Current position
- Phase: 1 — step 1 of 6
- Next action: Add the `coverlet.collector` 6.0.2 `PackageReference` block from plan.md Phase 1 Step 1.1 to `DbExplorer.Tests/DbExplorer.Tests.csproj` inside the existing `<ItemGroup>` that ends at line 22, then run the coverage command and confirm `DbExplorer.Tests/TestResults/<guid>/coverage.cobertura.xml` exists.
- Files mid-edit (if any): none

## Deviations from plan.md
(append-only; never delete entries)
- 2026-08-08 — Plan authored. One deliberate design deviation from the Sonar review's own recommendation is baked in and pre-approved: hotspot H3 was reviewed as "gate /search behind a named authorization policy", but plan.md Phase 7 implements `SearchOptions` feature flags instead. Reason: the repo has zero authorization-policy or role precedent (`DbExplorer/Program.cs:78` is a bare `AddAuthorization()`; `DbExplorer/Options/AuthOptions.cs:7-77` has no roles), while feature-flag Options is the established capability-gating pattern (`QueryBuilderOptions.Enabled` at `Options/QueryBuilderOptions.cs:11`, gating `MainLayout.razor:53` and `DiagramPage.razor:19-23`). Full rationale in plan.md §3.

## Discovered gotchas not in plan.md
(append-only; these are candidates for the next plan's Phase 0)
- (none yet)

## Open questions for the human
- **blocking: no** — `Microsoft.AspNetCore.Authentication.Negotiate` 10.0.8 has **two known high-severity advisories** (`NU1903`: `GHSA-2p3q-h3hg-jcqq`, `GHSA-8prm-248r-h957`), emitted as build warnings on every build of both `DbExplorer.csproj` and `DbExplorer.Tests.csproj`. This is explicitly Non-goal 1 — it is a dependency upgrade, not Sonar remediation, and a transport-auth bump carries its own regression risk. It should be ticketed separately and prioritised on its own merits. Do not upgrade it while working this plan.
- **blocking: no** — Can a live PostgreSQL and a live MySQL instance be made available for the §7 acceptance run? The single highest-value fix in this plan (issue 1 / hotspot H2 — MySQL backslash escaping in generated SQL literals) is otherwise verified by unit test only. If the answer is no, that fact must appear in the closing summary.
- **blocking: no** — Can the test SQL Server principal be given a database it can enumerate but not open (`DENY CONNECT` on `DbExplorerSonarDenied`, per plan.md §7 Artifact A)? Without it, issue 2's fix (per-catalog failure isolation) cannot be runtime-verified.

## Environment notes
- Platform: Windows 11, PowerShell primary shell. A Git Bash tool is also available; the two take different syntax.
- Solution: `DbExplorer.sln` — three projects, all `net10.0`, `LangVersion 13`, `Nullable enable`.
  - `DbExplorer` (Blazor Server web app), `DbExplorer.Core` (models/interfaces/pure logic), `DbExplorer.Tests` (xUnit 2.9.2 + FluentAssertions 6.12.1 + Moq 4.20.72).
- `DbExplorer/DbExplorer.csproj:13` declares `<InternalsVisibleTo Include="DbExplorer.Tests" />` — tests can reach `internal` types directly (this is how Phase 1's `internal static class SqlTextEscaper` is tested without making it public).
- **No `sonar-project.properties`, no `.editorconfig`, and no analyzer ruleset exist anywhere in the repo.** There is nothing in-repo asserting a rule set, so Sonar way defaults apply and there are no linter-vs-Sonar conflicts to reconcile.
- **No coverage tooling exists before Phase 1.** No `coverlet.collector` package, and no coverage report has ever been produced. Coverage is *not measured*, not zero — do not report a coverage percentage until Phase 1 Step 1.1 lands.
- Baseline test count is **190**. Phase 1 deletes two test files (`DataValueSearchServiceTests.cs`, `MetadataSearchServiceTests.cs` — 10 `[InlineData]` cases total) and replaces them with a strict superset, so the count must **rise**, never fall.
- Local secrets: `DbExplorer.csproj` sets `UserSecretsId fe452472-1ccb-47b4-b1c7-46453c24d590`, loaded at `Program.cs:16`. Connection strings live there or in `appsettings.Development.json` (only `appsettings.Development.example.json` is committed). A fresh machine will have no working connections until those are supplied.
- Audit logging is **on** in the committed `appsettings.json` (`"Audit": { "Enabled": true, "LogSql": true }`). Logs go to console and `logs/dbexplorer-.log` (daily rolling), configured at `Program.cs:19-26`.
- Runtime verification requires a live database. Phases 1, 4 and 5 can be fully verified without one; Phases 2, 3, 6 and 7 cannot.
- The app runs with `dotnet run --project DbExplorer`. There are also `run.cmd` / `run.ps1` helpers at the repo root (not inspected during planning — check them before assuming `dotnet run` is the intended entry point).
