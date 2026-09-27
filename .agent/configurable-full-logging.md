# Add opt-in full logging and separate server/client directories

This ExecPlan is a living document maintained according to `.agent/PLANS.md`. Keep `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` current throughout implementation.

## Purpose / Big Picture

Operators need a temporary diagnostic mode that records complete application events and HTTP exchanges so a True API rejection can be investigated, while normal production operation remains filtered and secret-safe. After this change, `Serilog:FullLoggingEnabled` in `appsettings.json` enables or disables full logging. Server files live below `logs/server` and client files below `logs/client`, making retention and collection independent and obvious.

## Progress

- [x] (2026-09-22 14:20Z) Read the logging sink, HTTP handler, configuration, logging tests, deployment documentation, and repository plan requirements.
- [x] (2026-09-22 14:25Z) Implemented dynamically configurable full-event and full-HTTP logging with a safe default of `false`.
- [x] (2026-09-22 14:27Z) Moved default server and client log paths into separate directories and updated tests/documentation.
- [x] (2026-09-22 14:30Z) Release test build completed with zero warnings/errors; complete suite passed 75/75 outside the sandbox.
- [x] (2026-09-22 14:32Z) Documented Debug web build completed with zero warnings/errors; Mock runtime returned HTTP 200 for `/`, `/console`, `/history`, and `/settings`, and the new server directory contained no unhandled exception.
- [x] (2026-09-22 14:36Z) Canonical packaging passed build, ACL regression checks, and 75/75 tests; the 422-entry ZIP has no forbidden runtime/secret files and contains the expected default switch and separate paths.

## Milestones

The first milestone adds `Serilog:FullLoggingEnabled` with a default value of `false`. The existing safe sink continues stripping exception messages and unapproved properties when false. When true, it forwards the original Serilog event, including exception details and structured properties. The outgoing HTTP handler will additionally record complete request/response URI, headers, and body in full mode. The setting is read from the reloadable configuration for every event/request so changing `appsettings.json` takes effect without restarting for newly emitted records.

The second milestone moves the configured file paths from `logs/server-.log` and `logs/client-.log` to `logs/server/server-.log` and `logs/client/client-.log`. Tests will locate the new paths and verify both routing and dynamic full-mode behavior.

The final milestone runs the documented builds and the complete Release suite outside the restricted sandbox, starts Mock mode and checks the relevant pages, then creates and inspects the canonical Windows ZIP without skipping tests.

## Surprises & Discoveries

- Observation: The current safe sink strips `SignatureLength`, which is why the production log displayed the literal `{SignatureLength}` placeholder.
  Evidence: `SigningAllowed` does not contain that property although the signer emits it.
- Observation: Forwarding raw Serilog events alone would not expose the True API error body because `LoggingHandler` currently emits only method, normalized endpoint, status, and duration.
  Evidence: the handler never reads HTTP headers or content.
- Observation: A log channel directory is created lazily only after its first event; a server-only signing test therefore has no client directory.
  Evidence: the first full run had 73 passing tests and two test-helper failures caused by `DirectoryNotFoundException` for an intentionally empty client channel. The helper now treats a missing channel directory as an empty log, and the next run passed 75/75.

## Decision Log

- Decision: Name the setting `Serilog:FullLoggingEnabled` and default it to `false`.
  Rationale: It belongs with the existing logging controls and preserves the current secure production behavior unless an operator explicitly opts in.
  Date/Author: 2026-09-22 / Codex
- Decision: In full mode, record unfiltered application events plus full outgoing HTTP request and response data, including headers and bodies.
  Rationale: The user explicitly requested complete logs, and the immediate diagnostic need is the discarded True API `403` response body. Documentation will clearly warn that tokens, signatures, certificate-related data, and business payloads can be present.
  Date/Author: 2026-09-22 / Codex
- Decision: Read the switch dynamically but keep file paths startup-bound.
  Rationale: Diagnostic mode can be enabled briefly without service restart, while Serilog file sinks cannot safely move existing handles at runtime; current documentation already states path changes require restart.
  Date/Author: 2026-09-22 / Codex

## Outcomes & Retrospective

Implementation is complete. `Serilog:FullLoggingEnabled` defaults to `false`, is evaluated dynamically, and switches both the event sink and outgoing HTTP handler between safe and full diagnostic modes. Full mode records original exceptions plus complete outgoing request/response URLs, headers, and bodies without consuming response content needed by the caller. Server and client files now live in independent subdirectories. Builds completed with zero warnings/errors, 75/75 tests passed, and all four Mock pages returned HTTP 200 without an unhandled exception. Canonical packaging produced `artifacts/windows/CrptWorkbench-win-x64.zip` with SHA-256 `2C973E0572D9EF744140C5429A41DE98AE5CC5F29E5D7B59333B4AB74F7D62E2`; the archive has 422 entries and no forbidden runtime or secret files.

## Context and Orientation

`src/CrptWorkbench.Web/WorkbenchLogging.cs` builds one Serilog destination from configuration and wraps it with `SafeLogSink`, which currently always filters events. `src/CrptWorkbench.Clients/HttpPipelineHandlers.cs` contains `LoggingHandler`, the outgoing production transport logger. `src/CrptWorkbench.Web/appsettings.json` defines two sub-loggers and file paths. `tests/CrptWorkbench.Tests/LoggingTests.cs` validates filtering, routing, independent levels, and path resolution. `docs/deployment/windows-production.md` is the operator guide.

“Full logging” means the original structured log event and complete outgoing HTTP exchange are written. It does not alter requests, retries, allowlists, signing, tokens, or operation behavior. It is intentionally unsafe for continuous production use because raw Authorization headers, True API tokens, signed challenges, code bodies, provider messages, and exception text can be recorded.

## Plan of Work

Modify `WorkbenchLogging.CreateLogger` and `SafeLogSink` so the sink has access to the live `IConfiguration`. If `Serilog:FullLoggingEnabled` is true, forward the original event. Otherwise retain the existing sanitization. Add `SignatureLength` to the safe signing allowlist so normal logs render that already non-secret numeric field correctly.

Modify `LoggingHandler` to receive `IConfiguration`. In full mode, buffer and log the request and response representations without changing the messages consumed by downstream code. Use separate structured properties for URI, headers, and body and do not add this behavior when the switch is false. Update any direct test construction or dependency injection assumptions.

Change the two configured file paths to their new directories. Update test helpers and assertions, add a test that toggles full mode on and off and proves secrets are absent before the toggle but raw exception/HTTP fields are present after it. Update deployment documentation with the new locations, switch semantics, dynamic behavior, and explicit data-exposure warning. Update the dated test baseline if test cases are added.

## Concrete Steps

Work from `D:\Артём\Projects\C#\СZHELPER`. Build with:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false

Run the complete suite outside the restricted sandbox:

    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

Build the server artifact without `-SkipTests`:

    PowerShell -NoProfile -ExecutionPolicy Bypass -File .\deploy\windows\Build-Package.ps1

## Validation and Acceptance

With `FullLoggingEnabled=false`, existing safe tests must prove exception messages, Authorization-like properties, and bodies remain absent. With it set to true and configuration reloaded, a new test must find the original exception message and structured secret values in the appropriate channel. An HTTP-handler test must prove the response error body can be captured only in full mode without preventing the caller from reading it afterward. Server files must exist only below `logs/server`; client files only below `logs/client`.

The web project must build with zero errors and no new warnings. All tests must pass. Mock runtime pages `/`, `/console`, `/history`, and `/settings` must return HTTP 200 without unhandled server exceptions. The resulting ZIP must contain no runtime database, keys, certificates, PINs, or tokens despite full logging support.

## Idempotence and Recovery

The changes are configuration-driven and safe to rebuild repeatedly. Turning the setting back to false stops new raw diagnostic events but does not erase existing full logs; operators must secure or remove those files according to their policy. No test contacts True API or changes certificates, profiles, services, or persistent production data.

## Artifacts and Notes

Expected default configuration:

    "FullLoggingEnabled": false

Expected paths relative to `Crpt:DataDirectory`:

    logs/server/server-YYYYMMDD.log
    logs/client/client-YYYYMMDD.log

## Interfaces and Dependencies

No Domain or Application interface changes are required. `LoggingHandler` gains constructor injection of `IConfiguration`, which ASP.NET Core DI already provides. No new package is required.

Revision note (2026-09-22): Initial plan created after reviewing current safe event filtering, HTTP logging, file routing, and tests.

Revision note (2026-09-22): Recorded implementation, separate paths, dynamic full-mode tests, the lazy-directory test-helper correction, and the successful 75/75 rerun. Runtime and packaging checks remain.

Revision note (2026-09-22): Recorded the clean Debug build and four-page Mock runtime check. Canonical packaging remains.

Revision note (2026-09-22): Closed the plan after successful canonical packaging, 75/75 repeated tests, archive configuration inspection, forbidden-file scan, and SHA-256 capture.
