# Continue file jobs past invalid marking codes

This living ExecPlan follows `.agent/PLANS.md`.

## Purpose / Big Picture

File jobs in “Запрос по кодам +”, including prepared TraceX sources, must validate each row before sending it, skip invalid rows and expose a separate durable report with original row numbers, bounded values and reasons. Remove loaded-code previews for this operation. Keep status counts, complete result exports, shipment checks and other operations intact.

## Progress

- [x] 2026-10-11: Locate fatal validation and reproduce with an intentionally failing regression test.
- [x] 2026-10-11: Implement bounded row consumption, filtering on both preparation and request reread, atomic per-batch rejection reports, profile-scoped paging and report download; hide file and bulk TraceX previews.
- [x] 2026-10-11: Web and Release test builds completed with zero warnings/errors; all 745 Release tests passed outside sandbox. Core routes returned HTTP 200; Mock browser upload, report paging and full report download verified; final diff has no whitespace errors or generated tracked artifacts.

## Surprises & Discoveries

BulkCodeJobProcessor.PrepareBatchAsync threw on a single invalid code, before queued batches were sent. Its bounded reader also threw at 512 bytes and lost the rest of a long row. The updated regression expected Completed and observed Failed before implementation. The workspace already includes unrelated shipment-filter changes and version 1.28.1; preserve them.

## Decision Log

Validate in bounded streaming batches rather than reading a gigabyte file into memory. Consume at most 1000 source lines per batch, drain oversized rows with a fixed buffer, and preserve original line numbers. Store report files atomically before the corresponding SQLite checkpoint; expose only report batches below the committed job NextBatchNumber. Rewrites before a checkpoint are idempotent; no database migration is required. Report values use JSON escaping and are capped at 512 bytes, with an explicit truncation reason. Reports remain local user data, never transport logs or technical history. The version becomes 1.29.0 because a new durable report and download are added.

## Outcomes & Retrospective

Completed: invalid rows are excluded before submission, jobs continue, reports persist with source row numbers, and loaded file/TraceX previews are hidden. The browser uploaded 252 rows with two valid codes and 250 invalid rows, completed with two processed codes, appended report pages from 100 to 200 with Enter, and downloaded exactly 250 report rows numbered 2 through 251. Core routes returned HTTP 200 and server logs had no unhandled errors. All 745 Release tests passed, with no failures or skips; both builds had zero warnings/errors. No real Production requests occurred. Already-failed jobs are not automatically restarted by the upgrade.

## Context and Orientation

src/CrptWorkbench.Application/BulkCodeJobProcessor.cs reads byte-position source batches and sends /cises/info through the existing Domain client. src/CrptWorkbench.Domain/BulkCodeJob.cs defines its storage interface. src/CrptWorkbench.Infrastructure/BulkCodeFileStore.cs owns local source/result files. BulkCodeFileService checks profile ownership before report reads. ApiConsole.razor renders jobs; BulkRejectedRows.razor appends report pages using FilePageCursor byte positions and ShowMore. Program.cs streams report downloads using existing protected, short-lived job/profile tickets. TraceXSqlImport.razor disables preview rendering and loading only for Bulk.

## Plan of Work

Regression tests in tests/CrptWorkbench.Tests/BulkCodeFileJobTests.cs must cover mixed valid/invalid rows, very long values, malformed UTF-8, invalid-only batches, exact request contents, byte/line counters, report pages 100/100/51, profile isolation and reopening. Build Web and Release tests, run the full suite outside sandbox, then start an isolated local Mock server and check core routes and logs.

## Concrete Steps

From D:\Артём\Projects\C#\СZHELPER run:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

The test command must run outside the sandbox with execution approval, per AGENTS.md.

## Validation and Acceptance

Mixed files finish Completed, only valid normalized codes reach the client, no invalid-only batch creates a request, and all skipped rows can be paged and downloaded. Long rows must not consume unbounded memory or merge into following rows. Reports must survive reopening, omit valid codes and reject reads by another profile. Core routes return HTTP 200 in Mock and logs contain no unhandled errors. Builds have zero errors and no introduced warnings; all tests pass.

## Idempotence and Recovery

Per-batch report writes replace atomically, before job checkpoint publication. Existing jobs without rejection files behave as empty reports. Deleting a job removes its reports with the existing validated job directory. Preserve all prior dirty files and do not commit without user request.

## Artifacts and Notes

The initial regression failed with Expected Completed, Actual Failed. Further evidence will be added after verification.

## Interfaces and Dependencies

Add WriteRejectedAsync and ReadRejectedPageAsync to IBulkCodeFileStore. Reuse FileRecordPage and FilePageCursor and existing Application ownership checks; add no provider dependencies to UI. Preserve existing production allowlists and shipment fragments.

Revision 2026-10-11: Plan records implementation decisions and remaining verification.

Revision 2026-10-11 completion: Recorded full-suite, build, browser, download and local runtime evidence.
