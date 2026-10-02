# Independent logging switches

This ExecPlan is a living document and follows `.agent/PLANS.md`.

## Purpose / Big Picture

Operators can turn server, UI, HTTP, CryptoPro, and SQL command events on or off independently in `appsettings.json`. SQL command events are off by default; database errors remain visible. The existing operation history continues to be stored in SQLite.

## Progress

- [x] (2026-10-01) Inspect Serilog routing, HTTP logging, signer diagnostics, configuration, and tests.
- [x] (2026-10-01) Add independent configuration and safe event filtering.
- [x] (2026-10-01) Replace old flag in tests and documentation; build and run the Release suite (490 passed).
- [x] (2026-10-01) Verify Mock runtime pages and inspect the final scoped diff.

## Surprises & Discoveries

- The previous full logging flag forwarded raw events and HTTP bodies. A source filter alone would not control the HTTP handler's separate detail logging.
- EF Core's default Microsoft warning override would suppress informational command events even when the new SQL switch was enabled; the command category needs an Information override.

## Decision Log

- Decision: Store SQL event IDs and severity, omitting SQL text and parameter values even when SQL event logging is enabled. Rationale: literals in query text and parameters may contain credentials or business data; the requested switch controls visibility of technical SQL events without weakening masking. Date/Author: 2026-10-01, Codex.
- Decision: Keep HTTP bodies out of diagnostics and log only allowlisted headers and endpoint paths. Rationale: arbitrary bodies and query strings may contain secrets. Date/Author: 2026-10-01, Codex.

## Outcomes & Retrospective

The configuration and tests are implemented. Release build passed with zero warnings and the suite passed 490/490 tests. A separate Mock instance returned HTTP 200 for `/`, `/console`, `/history`, and `/settings` with no unhandled server errors. The scoped diff contained no generated database or key files.

## Context and Orientation

`src/CrptWorkbench.Web/WorkbenchLogging.cs` routes Serilog events into server and UI files and strips sensitive properties. `src/CrptWorkbench.Clients/HttpPipelineHandlers.cs` creates HTTP diagnostic events. `src/CrptWorkbench.Infrastructure/CryptoProCertificateSignatureService.cs` creates signer diagnostics. `src/CrptWorkbench.Web/appsettings.json` supplies defaults. The `ApiRequestLogs` SQLite table records business operation history, not Serilog events.

## Plan of Work

Add a `WorkbenchLogging` configuration section. Apply switches at event emission so new events follow changed settings without restart. Use category-specific checks for EF command, HTTP, and signer events. Remove `Serilog:FullLoggingEnabled` references; preserve Serilog levels, file routing, and rotation. Update tests and deployment documentation. Raise the application version to `1.22.0` from the current working copy's `1.21.17`.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, build the web project with `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`. Build the Release tests with `dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false`. Run `dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Normal` outside the restricted sandbox because the test runner uses a Windows named pipe. Expect zero failures. Start a separate Mock instance and check `/`, `/console`, `/history`, and `/settings` for HTTP 200 and no unhandled server errors.

## Validation and Acceptance

`LoggingTests.SqlCommandEventsAreOptInAndErrorsRemainSafe` checks that normal SQL events require the switch, errors remain, and SQL literals are not written. `LoggingTests.IndependentCategoriesCanBeChangedDynamically` checks a live server switch. The full suite must pass. Inspect the final diff to exclude generated data and sibling projects.

## Idempotence and Recovery

All changes are configuration and code; there is no migration. Rebuilds and tests are repeatable. If the live Mock process is stopped, start it again without deleting its data directory.

## Artifacts and Notes

Release test evidence: 490 total, 490 passed, 0 failed, 0 skipped. No production network call is required.

## Interfaces and Dependencies

`WorkbenchLogging` exposes Boolean configuration keys `ServerEnabled`, `UiEnabled`, `HttpEnabled`, `CryptoProEnabled`, `SqlQueriesEnabled`, `HttpDetailsEnabled`, `ExceptionDetailsEnabled`, and `CryptoProviderMessagesEnabled`. All are read from `IConfiguration` when the event occurs. No public C# API or schema changes are required.
