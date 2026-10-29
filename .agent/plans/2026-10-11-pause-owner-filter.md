# Pause file jobs and filter codes by current owner

This living ExecPlan follows `.agent/PLANS.md` and implements the user-approved plan of 2026-10-11.

## Purpose / Big Picture

Users can pause TXT and prepared TraceX jobs, restart the app without restarting paused work, and continue without repeating saved batches. “Отгружен” means a found code is not owned by the active profile INN, including an explicitly absent owner. It no longer requires history or document requests.

## Progress

- [x] 2026-10-11: Inspect checkpoints, fragment format, UI, profile scope and owner DTO; capture dirty-worktree baseline.
- [x] 2026-10-11: Implement durable pause transitions and draining of in-flight requests; RED reproduced three original pause failures.
- [x] 2026-10-11: Persist owner evidence with backward-compatible fragments and consistent selection/export; processor regression RED then GREEN confirmed.
- [x] 2026-10-11: Integrate UI, version 1.30.0, ten new regression tests and independent review. Retry-after-429 test reproduced a second outbound request during pause; added conditional retry claim. Reviewer now reports Pass.
- [x] 2026-10-11: Final Web and Release test builds zero warnings/errors; all 755 Release tests passed outside sandbox. Mock browser pause/restart/resume, full 200,000-code export, rejection report and 380px layout verified. Final scoped diff check clean; generated artifacts ignored.

## Surprises & Discoveries

The current filter reconstructs movements, whereas the requested meaning is current ownership. CisInfo already contains OwnerInn, but file fragments persist only status and code. Existing working-tree changes include shipment checking and invalid-row reports; retain them. A missing legacy owner field cannot be treated as a new response explicitly lacking an owner.

## Decision Log

Pause uses PauseRequested and Paused strings in existing SQLite state fields, with atomic profile/mode transitions; no schema migration is needed. Drain sent requests and prevent unsent queued delegates from starting. Continue from queued prepared batches before reading more source. Current-owner evidence is written atomically in the same result fragment, with a version marker distinguishing legacy rows. Retain old shipment machinery for compatibility but stop calling it from the bulk filter. Missing profile INN blocks the filter; missing response owner passes it. Old ownerless fragments require a new query. No Production verification is authorized. Version increases to 1.30.0.

## Outcomes & Retrospective

Completed. Final Web and Release test builds have zero warnings/errors; the full Release suite passed 755 tests with zero failures/skips. A Mock browser uploaded 200,000 valid duplicate codes and 250 invalid lines, paused in queue, retained pause after server restart, continued, paused during processing with 29,000 completed codes and 32,000 prepared source rows, and completed with exactly 200,000 codes. The filtered protected-ticket TXT download contains exactly 200,000 rows. The rejection report starts at original row 200001. At 380px the document scrollWidth is 365. Core routes returned HTTP 200; server log has no unhandled/error/fatal entries. No Production requests occurred. Owner equality/absence, legacy rows, isolation and prepared TraceX behavior were verified automatically; live TraceX and these owner branches were not demonstrated interactively. The browser control exhibited severe delays on initial navigation/upload, then recovered. An attempted Settings edit did not demonstrate persistence and was not used as owner-equality proof.

## Context and Orientation

BulkCodeJobProcessor in Application prepares 1000-row byte-position batches and processes up to four concurrently. BulkCodeJobRepository in Infrastructure checkpoints them in SQLite. BulkCodeFileStore publishes atomic result and rejection fragments. BulkCodeFileService owns scoped user actions. ApiConsole.razor and its Shipment partial manage selection and polling; Program.cs streams protected-ticket exports. BulkCodeEntry must retain owner evidence for file selection. Existing shipment records represent movement evidence and must not be reused for ownership decisions.

## Plan of Work

Add conditional repository transitions for pause/resume and integrate processor checks before preparation and before each request. Paused jobs are excluded from scheduling; PauseRequested is recoverable after restart. Preserve batch counts and rejection files. Add service methods with current-mode checks and allow cancellation of paused jobs.

Extend BulkCodeEntry with OwnerInn and OwnerKnown; add WriteOwnedFragmentAsync to the file-store port and implementation. Keep old writers/readers compatible. Add streaming ownership summary and export methods to BulkShipmentFileService using profile INN, with no client calls and no old shipment-cache dependency. Cache counts per immutable fragment rather than repeatedly loading all rows where practical. Bind export to expected INN/mode and reject stale tickets.

Update processor result serialization to persist owner evidence, including empty owner as known. Update the UI partial to classify pasted results locally, refresh file ownership summaries, stop scans for paused jobs, render legacy warnings, and display pause/continue controls. Retain hidden file previews. Update version and AGENTS verification evidence only with actual results.

## Concrete Steps

Run from repository root:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

The test command requires outside-sandbox approval from its first invocation. First run targeted newly added tests to demonstrate failure, then implementation, then the full suite.

## Validation and Acceptance

Cover queued/running pause, repeated clicks, in-flight drain, unsent batches, restart, resume, cancellation/deletion, profile/mode isolation, duplicate and invalid rows. Cover owner equal/different/empty, failed-code exclusion, legacy unknowns, status intersection, paging and complete exports. Core routes return HTTP 200. In an isolated Mock instance verify keyboard pause/resume, persistent paused state, ownership filtering and download, 380px layout and safe logs. Builds require zero errors and no introduced warnings. Do not claim live CRPT proof.

## Idempotence and Recovery

Transitions are conditional and idempotent. Saved result fragments are reused when SQLite completion was interrupted. Legacy fragments remain readable for ordinary exports; their ownership cannot be inferred. Preserve all source and rejection files. Do not delete existing databases, keys or unrelated changes. No commits or pushes are requested.

## Artifacts and Notes

The pre-task tree has modified and untracked shipment/invalid-row files, version 1.29.0. Verification evidence will be recorded here as obtained.

2026-10-11 update: pause uses an atomic batch dispatch claim and recovers Running batches after restart. Deleting is an internal conditional state preventing resume/delete races. Owner summaries read saved fragments locally even on pause, so saved downloads remain available after reopening; they initiate no background network work. Retry dispatch is also gated after 429.

2026-10-11 verification update: recorded full 755-test success and actual browser evidence above. Test log and screenshot are ignored artifacts under artifacts/pause-owner-tests.log and artifacts/pause-owner-qa.png. Existing dirty changes are preserved; no commit or push performed.

## Interfaces and Dependencies

Use existing Domain ports, EF SQLite repositories, Application services and Blazor injection. Add BulkCodeFileService.PauseAsync/ResumeAsync with cancellation tokens, conditional repository methods scoped to profile/job/mode, BulkCodeEntry owner evidence, and IBulkCodeFileStore.WriteOwnedFragmentAsync. Ownership summary/export use current profile INN and integration mode; no additional network dependency is introduced.
