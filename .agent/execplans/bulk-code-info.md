# Add bulk code information operation

This ExecPlan is a living document maintained according to `.agent/PLANS.md`.

## Purpose / Big Picture

Users need to inspect more than 1000 marking codes without manually splitting files. The new «Запрос по кодам +» operation batches the existing read-only True API lookup, filters displayed statuses, and provides a complete copyable list. The existing single-code operation remains intact.

## Progress

- [x] (2026-09-28) Inspected the existing handler, UI, production allowlist, Mock client, and True API v733 section 5.1.2.
- [x] (2026-09-28) Added the separate batch handler, its production path guard, and focused tests.
- [x] (2026-09-28) Added status controls, paged cards, full copy list, cancellation, and per-code history action. Verified build, tests, Mock runtime and history persistence.

## Surprises & Discoveries

- The production client checks both operation ID and path; the new ID needs a narrowly scoped exception for the existing `/cises/info` path.
- The working tree contains unrelated edits; preserve them.

## Decision Log

- Decision: keep the existing single lookup unchanged and register `true-api.code-info-bulk` separately. Rationale: the user explicitly chose a distinct catalog operation. Date/Author: 2026-09-28 Codex.
- Decision: cap concurrent batches at four even on larger servers. Rationale: CPU count is a useful adaptive ceiling, while the API is network bound and external limits are not determined by CPU count. Date/Author: 2026-09-28 Codex.

## Outcomes & Retrospective

The separate operation works in Mock, where a two-code request showed two results and no live traffic. The status filter reduced the copy list from two to one; opening a code loaded its document history in the original operation. The isolated web build had zero warnings/errors, and the Release suite passed 415 tests with zero failures. A standard Debug build encountered a DLL lock from a separate running process, so final verification used an isolated output path without stopping that process. Live CRPT remains unverified.

## Context and Orientation

`src/CrptWorkbench.Application/TrueApiCodeInfoHandler.cs` contains the current one-request lookup. `src/CrptWorkbench.Web/Components/Pages/ApiConsole.razor` renders operation forms and results. `src/CrptWorkbench.Clients/ProductionTrueApi.cs` restricts live operations. `src/CrptWorkbench.Clients/MockClients.cs` supplies local examples. `src/CrptWorkbench.Application/Operations.cs` records one history entry and its individual exchanges.

## Plan of Work

Add a new application handler to validate a newline-separated list, send ordered batches of at most 1000 through the same True API client, retry throttled batches with bounded delays, and reject incomplete responses. Register it and allow only its existing read-only endpoint in Production and Mock. Add its input validation. In the console, show status checkboxes, a paged result, and a full copyable code list; reuse the single lookup link for history. Add focused tests and run the documented Release build and suite.

## Concrete Steps

From repository root `D:\Артём\Projects\C#\СZHELPER`, run `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`, then the documented Release test build and test commands in `AGENTS.md`. Start the app in Development and check the operation through `/console?operation=true-api.code-info-bulk`.

## Validation and Acceptance

1001 valid codes create two requests; each has at most 1000. Filtering one status leaves only that status in cards and copied codes. Every batch uses one correlation ID. A failed or incomplete batch results in an error, never a partial successful list. Existing single lookup and Mock behavior still pass their tests. Release build has zero errors; Release tests have zero failures.

## Idempotence and Recovery

Changes are additive; rerun build and tests safely. No schema migration or production call is needed. Preserve unrelated dirty files.

## Artifacts and Notes

True API v733.0 section 5.1.2 states 1–1000 normalized codes per POST `/cises/info` request.

The local Mock browser request persisted `true-api.code-info-bulk` with status 200, success 1, and nonempty response/exchange JSON in the Release SQLite history database.

Revision 2026-09-28: recorded completed implementation, verification, and the Debug DLL lock after final test execution.

## Interfaces and Dependencies

`TrueApiBulkCodeInfoHandler : IApiOperationHandler` uses `ITrueApiClient`; its descriptor ID is `true-api.code-info-bulk`. The response remains the existing array of `CodeInfoItem`. The UI remains transport-free.
