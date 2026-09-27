# Add TraceX setup, special-feature placeholders, and API favorites

This ExecPlan is a living document. The sections `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds. Maintain this document in accordance with `../PLANS.md`.

## Purpose / Big Picture

After this change, an operator can open placeholder work areas for future TraceX and complex aggregation workflows, securely configure and explicitly test a TraceX database connection for the selected profile, and bookmark API Console operations across True API, National Catalog, and SUZ. Favorites persist per profile, while connection strings remain encrypted and are never rendered back into the UI or written to logs.

## Progress

- [x] (2026-09-15 14:39Z) Inspected the existing navigation, profile secret storage, API operation registry, SQLite initialization, and baseline tests.
- [x] (2026-09-15 14:44Z) Added domain/application contracts and services for TraceX connection settings and favorite operations.
- [x] (2026-09-15 14:49Z) Added Infrastructure persistence, provider-specific TraceX connection checking, and EF Core migrations with legacy-database adoption.
- [x] (2026-09-15 14:52Z) Added navigation, placeholder pages, profile settings, favorite Console UI, and live profile-change handling.
- [x] (2026-09-15 14:54Z) Added eight focused persistence, service, connection-checking, and migration tests.
- [x] (2026-09-15 15:02Z) Built with zero warnings/errors, passed all 61 tests, exercised every relevant route, visually verified desktop/mobile and both themes, inspected logs, and removed temporary runtime data.

## Surprises & Discoveries

- Observation: The repository is nested under a broader uninitialized Git working tree, so normal `git status` reports unrelated sibling directories and requires a safe-directory override.
  Evidence: `git status --short` reported dubious ownership for `D:/Артём/Projects/C#`, and the parent branch has no commits.
- Observation: The baseline test suite needs to run outside the restricted sandbox because Microsoft Testing Platform uses a named pipe.
  Evidence: The sandboxed test run started zero tests with `UnauthorizedAccessException`; the approved run passed all 53 tests.
- Observation: The new long navigation label was clipped at the original sidebar width.
  Evidence: Desktop browser verification showed “Специальные возможнос”; increasing the expanded sidebar to 300px and applying a compact label size made the complete label visible.
- Observation: Running the Debug build with a Production environment does not serve development static assets correctly.
  Evidence: the first runtime check returned page HTML but logged missing development static assets; rerunning with `ASPNETCORE_ENVIRONMENT=Development` served interactive Blazor assets and allowed the favorite action to persist.

## Decision Log

- Decision: Favorites store only `(ProfileId, OperationId)` and remain a UI view rather than a new `ApiSystem` value.
  Rationale: This matches the agreed bookmark semantics and avoids contaminating integration routing with a presentation concept.
  Date/Author: 2026-09-15 / Codex
- Decision: Store the TraceX provider on `CrptProfile` and the full connection string only in `ISecretStore`.
  Rationale: The provider is harmless profile metadata; credentials and endpoints require Data Protection encryption and must never be returned to the browser.
  Date/Author: 2026-09-15 / Codex
- Decision: Support SQL Server and PostgreSQL through provider-specific ADO.NET drivers in Infrastructure, and test only `SELECT 1` after an explicit user action.
  Rationale: This validates network, authentication, and query execution without reading or modifying business data.
  Date/Author: 2026-09-15 / Codex
- Decision: Replace `EnsureCreated` with migrations and adopt known legacy schemas by recording only the baseline migration before applying feature migrations.
  Rationale: New installations need the full schema, while existing databases must be upgraded without recreating or losing tables.
  Date/Author: 2026-09-15 / Codex

## Outcomes & Retrospective

Delivered both placeholder work areas, per-profile cross-system operation favorites, protected TraceX configuration for SQL Server/PostgreSQL, explicit safe connection checking, and backward-compatible SQLite migrations. The final web and test builds contain zero warnings/errors; all 61 tests pass. Runtime checks returned HTTP 200 for all six pages plus live/ready health endpoints, the browser confirmed favorite persistence and source-system labeling, both themes, readable desktop navigation, and a 390px mobile layout. No real TraceX database was contacted because no user credentials were supplied, and no CRPT production request was made. Temporary runtime database and key files were removed after verification.

## Context and Orientation

The Blazor shell and navigation live in `src/CrptWorkbench.Web/Components/Layout/MainLayout.razor`; the API Console and profile settings are `src/CrptWorkbench.Web/Components/Pages/ApiConsole.razor` and `Settings.razor`. Domain entities and persistence ports are in `src/CrptWorkbench.Domain`. Application services coordinate those ports without depending on provider-specific code. SQLite, Data Protection, and Windows/platform integrations live in `src/CrptWorkbench.Infrastructure`, and registrations are composed in `src/CrptWorkbench.Web/Program.cs`.

The existing `ProtectedSecrets` table stores encrypted values through ASP.NET Core Data Protection. The current database bootstrap uses `EnsureCreated`, which cannot evolve an existing schema. This change introduces an initial migration matching the current database and a second migration that adds the TraceX provider column and favorites table. A legacy database means one with the three known current tables but no EF migrations history table.

## Plan of Work

Extend the domain with `TraceXDatabaseProvider`, `FavoriteApiOperation`, `IFavoriteApiOperationRepository`, deletion on `ISecretStore`, and an `ITraceXConnectionTester` contract. Extend `CrptProfile` with provider metadata only. In Application, add `FavoriteApiOperationService`, expand `ProfileService` with save/clear/status/check methods for the protected TraceX secret, and make `WorkbenchContext.ProfileId` publish changes.

In Infrastructure, implement favorite persistence with a composite key, protected-secret deletion, and `TraceXConnectionTester`. The tester creates either `SqlConnection` or `NpgsqlConnection`, opens it asynchronously, executes exactly one `SELECT 1`, uses a linked ten-second cancellation window, and returns a sanitized result without logging connection details. Add matching provider packages and DI registrations.

Add EF Core migrations. The baseline creates Profiles, ApiRequestLogs, and ProtectedSecrets as they exist before this work. The feature migration adds `TraceXDatabaseProvider` to Profiles and creates FavoriteApiOperations. Update database initialization so an empty database is migrated normally; a known legacy database is transactionally baselined and migrated; and any partial/unknown schema fails without destructive repair. Seed the demo profile after migration.

Add `/tracex` and `/special-features` pages and navigation entries. Add a TraceX settings card with provider selection, write-only connection-string input, configured-state indicator, clear action, and explicit test action. Add a UI-only Favorites tab to the Console, accessible star controls beside operations, per-profile loading, immediate profile-switch refresh, and empty/removal behavior. Add only the CSS needed for the new cards, operation-row controls, and responsive layout.

Add focused tests for favorites, encrypted secret lifecycle, ProfileService orchestration and redaction, provider selection/query/cancellation through injectable connection creation, and both new and legacy database migration paths.

## Concrete Steps

Run all commands from `D:\Артём\Projects\C#\СZHELPER`.

Restore packages after project-file changes:

    dotnet restore CrptWorkbench.sln -p:RestoreUseStaticGraphEvaluation=true

Build the web project and tests:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false

Run all tests outside the restricted Windows sandbox when necessary:

    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

Start the app and request `/`, `/console`, `/tracex`, `/special-features`, `/history`, and `/settings`. Each route must return HTTP 200 and the server log must contain no unhandled exception.

## Validation and Acceptance

All 53 baseline tests and eight added tests pass with zero failures; builds complete with zero errors and no newly introduced warnings. A favorite added under one profile persists across page reloads, appears in the combined Favorites view with its source system, and does not appear under another profile. Removing the selected favorite selects the next favorite or clears the editor.

The settings UI never receives an existing connection string. Saving a new string produces an encrypted `ProtectedSecrets.ProtectedValue` that contains neither the plaintext password nor full original connection string. Changing provider without a replacement string is rejected. Clear removes the protected row. Check uses the saved provider and secret, runs only after a click, executes `SELECT 1`, times out within ten seconds, and reports only sanitized success/failure text.

New databases contain the complete migrated schema and seed profile. A legacy `EnsureCreated` database retains its profiles, history, and protected secrets after migration and gains the new column/table. Unknown schemas fail without table deletion. No authentic CRPT endpoint is added or contacted.

## Idempotence and Recovery

Favorites use a composite primary key and idempotent add/remove methods. Migrations and demo seeding can run repeatedly. Legacy adoption records the baseline only after confirming the exact expected table set and does so in a transaction. If startup sees an unknown schema, preserve the database and report the incompatibility; never delete or recreate it automatically. Re-entering a TraceX connection string replaces only that profile's protected value.

## Artifacts and Notes

Baseline evidence before edits:

    Build succeeded. 0 warnings, 0 errors.
    Test summary: total 53, failed 0, passed 53.

Final evidence:

    Web build succeeded. 0 warnings, 0 errors.
    Test build succeeded. 0 warnings, 0 errors.
    Test summary: total 61, failed 0, passed 61.
    Routes /, /console, /tracex, /special-features, /history, /settings: HTTP 200.

## Interfaces and Dependencies

`CrptWorkbench.Domain` will expose `TraceXDatabaseProvider`, `FavoriteApiOperation`, `IFavoriteApiOperationRepository`, `ITraceXConnectionTester`, and `ISecretStore.DeleteAsync`. The connection tester accepts provider plus plaintext connection string only inside the server-side application boundary and returns a result containing no secret. `FavoriteApiOperationService` validates operation IDs against `IApiOperationRegistry` before persistence.

`CrptWorkbench.Infrastructure` will reference `Microsoft.Data.SqlClient` 7.0.3, `Npgsql` 10.0.3, and the EF Core design package matching the existing 10.0.11 runtime packages. Provider-specific connection classes stay out of Domain, Application, and Razor components.

Revision note (2026-09-15 15:02Z): completed every milestone, recorded runtime discoveries, final verification evidence, and the implementation outcome.
