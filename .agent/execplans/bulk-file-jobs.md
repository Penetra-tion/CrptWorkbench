# Process very large bulk code files as durable jobs

This ExecPlan is a living document maintained according to `.agent/PLANS.md`. Keep Progress, Surprises & Discoveries, Decision Log, and Outcomes & Retrospective current.

## Purpose / Big Picture

An operator can upload a UTF-8 file containing up to 150 million marking codes, leave the page, and later download code lists by status. The application reads bounded batches and resumes after restart instead of putting the file, responses, and exchange history into one Blazor circuit or SQLite history row. The existing pasted small-list operation remains available.

## Progress

- [x] (2026-09-28) Inspected the current 10 MB file path, bulk handler, history, profile boundaries, worker, and migration setup.
- [x] (2026-09-28) Added additive SQLite job/batch storage, source and atomic result fragments, and a four-request background processor.
- [x] (2026-09-28) Added profile-scoped streaming upload, progress, retry, cancel, delete, and ticketed streamed download UI.
- [x] (2026-09-28) Added focused tests; Release build and 419 tests passed. Mock runtime routes returned HTTP 200, the form was inspected in a browser, and the final file review found no generated data in the source tree.

## Surprises & Discoveries

- The current file path calls `ReadToEndAsync`, then the handler splits and materializes every batch. The executor stores the aggregate response and every exchange in SQLite. Merely raising `OpenReadStream` would multiply memory and history size.
- There is no HTTP identity linked to the selected Blazor profile. Download links therefore need short-lived signed tickets created only after a profile-scoped lookup.
- SQLite cannot sort mapped `DateTimeOffset` fields. Jobs use an indexed Unix-millisecond field for ordering, as existing request history does.
- The browser automation surface displayed the new form but could not select a local file through the native file chooser. Service-level tests exercised the complete upload and worker paths; interactive file selection remains a manual UI check.

## Decision Log

- Decision: retain the existing pasted-code handler and add a separate file-job service. Rationale: it avoids changing small interactive behavior or the production endpoint allowlist. Date/Author: 2026-09-28 Codex.
- Decision: store one atomic status-list fragment per completed request batch and a SQLite batch checkpoint. Rationale: a restart can recognize an already written fragment and avoid resending a completed request; exports can stream fragments without materializing the full result. Date/Author: 2026-09-28 Codex.
- Decision: keep source and result files until profile-scoped manual deletion; process later batches after an individual failure and offer retry. Rationale: the user chose these behaviors. Date/Author: 2026-09-28 Codex.
- Decision: use a 64 KiB asynchronous line reader with logical byte offsets and 512-byte line storage. Rationale: precise checkpoints and bounded input memory avoid scanning from the start after restart. Date/Author: 2026-09-28 Codex.
- Decision: stop the job on disk I/O failure and allow continuation after space is restored; retain per-batch retry for remote failures. Rationale: continuing to create failed batches on a full disk would waste requests and time. Date/Author: 2026-09-28 Codex.

## Outcomes & Retrospective

The file path now streams source data to the configured data directory, persists batch checkpoints, and writes status-list fragments without retaining all codes or response bodies in memory or history. Four new tests cover a 64 MB generated stream, bounded upload read size, 1000-code batches and concurrency, fragment recovery, failed-batch retry, cancellation, invalid characters, and profile isolation. The Release test build completed with zero warnings/errors; 419 tests passed with zero failures. A separate Mock application returned HTTP 200 on `/`, `/console?operation=true-api.code-info-bulk`, `/history`, and `/settings`, and the new file-job controls were visible at a narrow browser width. No live CRPT request or 150-million-code end-to-end run was performed; target server capacity, CRPT rate limits, and long-duration throughput remain to be measured there.

## Context and Orientation

`src/CrptWorkbench.Web/Components/Pages/ApiConsole.razor` currently reads bulk files into the `codes` field. `src/CrptWorkbench.Application/TrueApiBulkCodeInfoHandler.cs` sends up to 1000 codes per existing `/cises/info` call. `src/CrptWorkbench.Application/Operations.cs` records aggregate responses in `ApiRequestLogs`; it must not be used for million-code file jobs. `src/CrptWorkbench.Web/Program.cs` selects the Mock or approved Production `ITrueApiClient`, registers the SQLite context, and already hosts one background worker. `src/CrptWorkbench.Infrastructure/WorkbenchDbContext.cs` and `Migrations` hold reviewed schema evolution.

The local True API v733.0 reference, section 5.1.2, defines the one-to-1000-code `/cises/info` request; this change reuses that contract without adding a production endpoint.

## Plan of Work

Add Domain job/batch records and persistence ports. Add an additive migration, repository, and file store under configured `Crpt:DataDirectory`. A job owns its uploaded source, next byte offset, count, and state. Each batch holds its source offsets and completion state. A worker prepares one batch at a time from the source, sends at most four concurrent existing read-only requests, writes a batch status fragment atomically, then marks the batch complete. On startup it finds unfinished jobs and resumes; a fragment left by a crash is committed without another API call. Failed batches remain retryable. The UI uploads a file as a stream, lists profile-owned jobs and progress, and requests short-lived signed download tickets. A download endpoint checks the ticket and streams selected status codes from fragments. No new CRPT path, host, or method is introduced.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, run `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`. Build the Release test project with `dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false`. Run the documented Release `dotnet test --project ... --no-build` outside the sandbox. Start a separate Mock instance in Development and verify `/console?operation=true-api.code-info-bulk`, `/`, `/history`, and `/settings` return HTTP 200 with no unhandled exceptions.

## Validation and Acceptance

Upload a file greater than 10 MB in Mock. The page shows a durable job, progress, and a downloadable status list after leaving and returning. A restart resumes without repeating a completed batch. Invalid UTF-8 or codes are reported with line numbers and without an invalid network request. Failed batches do not suppress successful output; retry only targets failures. Cancel and delete are scoped to the active profile. Peak managed memory for a generated large input remains bounded relative to file length. The Release suite has zero failures, and no authentic CRPT request is used as validation.

## Idempotence and Recovery

Migration is additive. Upload uses a temporary file and atomic rename. Fragment writes use temporary files and atomic rename, and incomplete temp files are ignored on restart. A failed batch can be retried; completed batches are never resent. Keep source files and results until a profile-scoped manual delete. Preserve unrelated working-tree content.

## Interfaces and Dependencies

`IBulkCodeJobRepository` in Domain exposes profile-scoped list/get/mutate operations and atomic batch checkpoints. `BulkCodeFileService` in Application coordinates upload, job actions, and export without knowing HTTP. `BulkCodeJobProcessor` in Application uses `ITrueApiClient`, existing code parsing, and the file store; Web supplies a hosted worker and endpoint. Infrastructure implements the repository and disk store with EF Core and the configured data directory. Razor uses only the Application service.

Revision 2026-09-28: initial implementation specification from the agreed plan.
Revision 2026-09-28: recorded completed implementation, verification, and the native file-chooser limit of automated browser testing.
