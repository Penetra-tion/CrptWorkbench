# Получение полного дерева агрегации

This ExecPlan is maintained under `.agent/PLANS.md`. Its living sections must stay current through implementation.

## Purpose / Big Picture

An operator can enter any supported marking or package code in a new True API operation, see its descendants and parent chain, optionally include sibling branches, and download tab-separated leaf-to-root paths. The default is the entered subtree plus its ancestors; the checkbox enables the full top-level tree. Mock remains default and no live CRPT calls are part of verification.

## Progress

- [x] (2026-10-02) Inspected registry, executor, history, code models, Mock and production guards; agreed behavior with user.
- [x] (2026-10-02) Implemented flat result contracts, cached iterative traversal, progress, TXT and transport registration.
- [x] (2026-10-02) Integrated virtualized tree in Console and History, cancellation and profile isolation.
- [x] (2026-10-02) Added focused traversal, export, persistence and transport tests; final full Release suite: 559 passed, 0 failed, 0 skipped; documented builds have 0 warnings/errors.
- [x] (2026-10-02) Verified interactive Mock workflow, version 1.24.0 and dated baseline. Final scoped diff and ignored artifacts checked; browser file-save capture is unavailable and reported separately.

## Surprises & Discoveries

The ordinary code handler also requests document history for each code. Tree traversal must call the Domain client directly rather than reuse that handler. The transport currently restricts operation identifiers as well as URLs; the new identifier needs the same strict body validation as bulk lookup. Existing history stores response JSON and needs no migration.

The working tree contains substantial pre-existing changes in authentication, logging, bulk jobs, deployment and other UI. Initial Git scope incorrectly appended СZHELPER relative to the current repository subdirectory; corrected checks use `git status -- .` from the actual project root, preserving all those changes. No commits or cleanup are performed.

Independent review found full TXT materialization could exhaust server memory on deep trees. `OpenTxtStream` now emits one row at a time, with chunked-read byte/cancellation tests. Initial expansion now follows unique observed ancestry even if a link is disputed, so the entered code remains visible. In-app browser native AX indices disagreed with the DOM; further checks use documented Playwright locators. The in-app download event timed out despite no browser error, so file-save verification needs a supported download surface.

## Decision Log

Decision: use `true-api.aggregation-tree` in the existing registry and API Console. Rationale: user selected a separate operation; existing forms support checkboxes. Date/Author: 2026-10-02, user and primary agent.

Decision: limit to 100000 unique requested codes, batch by 1000, run at most min(processor count, 4) batches. Preserve partial results on fetch failures; cancellation throws and writes no history. Rationale: agreed interactive scope and cancellation contract. Date/Author: 2026-10-02, primary agent.

Decision: export only confirmed paths, UTF-8 without BOM, tab delimiters, CRLF, no header. Missing/error nodes are not leaves. Rationale: an incomplete export must not fabricate aggregation. Date/Author: 2026-10-02, user and primary agent.

Decision: require explicit composition for non-UNIT codes; reject mismatched returned Cis as unconfirmed identification. Rationale: optional or malformed responses must not turn an unknown aggregate into a false terminal path. Date/Author: 2026-10-02, primary agent and independent test review.

Decision: stream TXT row by row through a nonseekable Application stream; use existing browser Blob download helper. Rationale: keep total export size out of server memory without introducing temporary-file persistence or endpoints. Date/Author: 2026-10-02, primary agent after security review.

Decision: check access-stop flag before every batch attempt, including delayed 429 retries. Rationale: a denied concurrent batch must prevent subsequent requests in other workers, while preserving responses already obtained. Date/Author: 2026-10-02, primary agent; covered by ConcurrentAccessFailureStopsAnAlreadyWaitingThrottleRetry.

## Outcomes & Retrospective

Implementation complete. Final full Release run: 559 passed, 0 failed, 0 skipped. Both documented builds: zero warnings/errors. Browser confirmed default leaf mode (4 nodes), sibling mode (9 nodes), keyboard expansion, History reopening, progress, cancellation and profile switch while running (no stale result; new profile history remains empty). A 101-node box renders only 20 DOM treeitems. Unknown code yields a partial result with disabled TXT. At 380x760 the document width is 365 with no horizontal overflow. Routes /, /console, /history, /settings and /operations return 200; server/client files show no unhandled errors. Temporary Mock instance stopped. No live integration evidence is implied by Mock or tests.

Browser download controls completed without a new browser/server exception, but the only available browser (Codex in-app) did not expose a download event or saved-file path. File-save verification remains unavailable; exact output bytes and chunked/cancelled stream behavior are covered by tests. Proof screenshot is stored outside Git in the session visualization folder as aggregation-tree.png.

## Context and Orientation

The root is `D:\Артём\Projects\C#\СZHELPER`; Git root is its parent and all Git operations must be scoped to СZHELPER. `src/CrptWorkbench.Application/Operations.cs` defines descriptors, context and the executor that masks and saves one history entry. `CodeInfoResults.cs` parses `/cises/info`; Domain `CisInfo` contains direct `Child` and `Parent` relations. `src/CrptWorkbench.Clients/ProductionTrueApi.cs` checks operation identifiers, method, URL and body before authentication. `MockClients.cs` supplies local code responses. `ApiConsole.razor` discovers forms and displays typed results; `History.razor` uses `SavedResultView`. Clients reference Domain only; UI calls Application only.

The local True API v733.0 reference `docs/crpt-reference/true-api-gis-mt-v733-2026-09-25.html`, section 5.1.2, specifies POST `/cises/info`, a JSON array of at most 1000 normalized 18–74 character codes, and direct child/parent fields. No pg parameter or new endpoints are needed.

## Plan of Work

Create `AggregationTree.cs` in Application with request, progress, flat nodes/links/result, safe parsing, visible-row indexing and leaf-to-root export. Create `TrueApiAggregationTreeHandler.cs` with an iterative parent ascent followed by breadth-first descendant fetching. Cache each requested code, match replies by requestedCis, mark ambiguity/missing responses, detect conflicting parents and cycles, and stop scheduling after authentication errors. Retry only 429 twice at 1 and 2 seconds. Keep one correlation ID and preserve masked exchanges through the executor.

Extend ApiOperationContext and ExecuteApiOperationService with optional IProgress<ApiOperationProgress> preserving callers. Register the handler in Web Program. Add the identifier to Mock's code dispatch and Production's strict bulk-shaped guard without changing host/path authority. Add a separate coherent Mock tree using distinct codes so existing fixtures remain unchanged.

Implement AggregationTreeResultView in Web. Build visible rows iteratively, expand only selected branches and render them with Virtualize; show type/status/count, selected input, omitted siblings and errors. Reuse existing text download helper or streamed download helper for large TXT. API Console wires progress, cancellation, typed parsing and stale-run guards; History uses the same component. Delete the upcoming catalog placeholder.

Add tests in `AggregationTreeTests.cs` and a focused persistence/guard companion as needed. Bump Directory.Build.props from 1.23.0 to 1.24.0. Update AGENTS dated baseline only after actual test result.

## Concrete Steps

Run from the repository root:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

Tests must run outside the restricted sandbox with approval on the first attempt. Expected: zero errors, zero failed tests; record actual passed count. If Debug output is locked, preserve the user's instance and use a separate output directory for runtime checks.

## Validation and Acceptance

Verify leaves, groups, sets, transport packages and ATK inputs; GS normalization; both sibling modes; multilevel paths; cache and request bounds; 100000 boundary; cycle, conflicting parent, duplicate response, invalid JSON, missing reply; 429 retry and 401/403 stop; cancellation writes no history. Check exact TXT bytes, variable depths and exclusion of invalid paths. SQLite reopening restores a masked profile-scoped tree. Production fake transport accepts only exact approved POST JSON bodies and rejects invalid requests before token acquisition.

Start an isolated Mock instance and exercise Console and History in a browser: enter leaf and package, toggle sibling mode, expand branches, cancel, download and reopen saved results, switch profile, inspect narrow viewport. Verify `/`, `/console`, `/history`, `/settings`, `/operations` return 200 and no unhandled errors appear. HTTP checks alone do not prove interaction. Confirm Mock headers and no authentic requests. Inspect final scoped diff for unwanted generated files.

## Idempotence and Recovery

No schema changes, package upgrades or destructive cleanup. Preserve existing dirty changes and runtime data. Reload starts a fresh read-only tree request; no restart-resume task is added. On transport failure retain known nodes with explicit incompleteness. User cancellation discards the run.

## Artifacts and Notes

Record build/test/browser evidence here when obtained. Full network verification is outside scope.

Final documented Web build: 0 warnings, 0 errors. Final Release test build: 0 warnings, 0 errors. Final Release test command outside sandbox: 559 passed, 0 failed, 0 skipped, duration 22.037 seconds. Earlier runs (555 and 558) preceded additional acceptance tests. Mock headers and masked history are asserted in SQLite tests. Generated App_Data, database/keys/logs and bin/obj remain ignored; pre-existing dirty files are preserved.

## Interfaces and Dependencies

Use existing ITrueApiClient.ExecuteAsync, System.Text.Json, registry, executor, masker and history repository. Request has Code and IncludeSiblings. Nodes contain code, type/status, known children, parent, load/diagnostic state; links contain parent/child and validity. Result contains requested code, mode, root, flat nodes/links, completeness and diagnostics. Optional progress contains stage and processed count. All fetching accepts CancellationToken. No Application reference may be added to Clients.

Revision 2026-10-02: initial execution specification created before implementation from the approved plan.

Revision 2026-10-02: implementation milestones, streaming/identity decisions, pre-existing worktree boundaries and initial verification evidence recorded.

Revision 2026-10-02: final 559-test baseline, stop-before-retry acceptance, browser evidence and explicit file-save capture limitation recorded.

Final independent security review: no blocking findings. Server TXT generation emits one row at a time; the browser download helper still buffers the complete file, so exceptionally large exports can exceed browser memory. Final scoped git diff --check passed (exit 0).
