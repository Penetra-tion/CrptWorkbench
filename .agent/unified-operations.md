# Unified operations entry point

This living ExecPlan follows `.agent/PLANS.md`. Maintain Progress, Surprises & Discoveries, Decision Log and Outcomes & Retrospective while implementing.

## Purpose / Big Picture

Users should choose a task in one catalog without knowing whether it uses a simple request or a document workflow. `/operations` groups information lookups, packages and code correction. Existing forms, technical results and backend behavior remain available.

## Progress

- [x] (2026-09-27) Inspected navigation, console query handling, registry and existing changes; saved scoped baseline patch.
- [x] (2026-09-27) Created `codex/unified-operations` from master with existing work preserved.
- [x] (2026-09-27) Added catalog, unified navigation, return links and explicit operation URL selection.
- [x] (2026-09-27) Added 12 navigation regression cases; updated AGENTS.md baseline to 319.
- [x] (2026-09-27) Debug Web and Release test builds: 0 errors, 0 warnings. Release tests outside sandbox: 319 passed, 0 failed.
- [x] (2026-09-27) Verified Mock routes, all seven card transitions, return links, active menu, history, persisted favorite, redirect, code links, both themes, keyboard focus and 380px layout. Saved screenshots.
- [x] (2026-09-27) Checked final scoped diff and baseline preservation; runtime artifacts remain ignored.

## Surprises & Discoveries

The current checkout had six dirty files: Directory.Build.props, CodeCorrection.razor, CodeCorrection.razor.cs, CodeCorrection.razor.css, Home.razor and PackageAggregation.razor. Home already lacked its old console button. Repository guidance described a parent Git root, but live git rev-parse --show-toplevel returned this project directory. Git metadata is read-only in the sandbox; creating the authorized branch required approved filesystem permission. Console query processing originally ran only during initialization; operation selection must also handle navigation between query strings in an existing component.

## Decision Log

2026-09-27: Keep the current checkout and create a branch without committing or stashing user work, as requested. Store the initial scoped binary diff in ignored artifacts/unified-operations/baseline.patch. Work only inside CZHELPER.

2026-09-27: Keep the API registry unchanged. The Web catalog uses its descriptors for lookup cards and ordinary links for document workflows; this avoids fictional executable handlers and persistence changes. Keep existing console favorites only. Use two-column cards on desktop and one column below 700px.

2026-09-27: Add a pure Web query resolver with code-link precedence, case-insensitive operation matching and explicit unknown operation state. Process changed query strings in OnParametersSetAsync. Opening an operation selects its form without execution; code links retain automatic lookup. Use a NavLink subclass for active highlighting across the six operation routes, including the legacy redirect.

## Outcomes & Retrospective

Completed the shared catalog and navigation in codex/unified-operations. Both builds passed without warnings; all 319 Release tests passed. Seven cards, legacy redirect, history code links, unknown operation recovery, favorites persistence, keyboard focus and responsive layouts passed browser checks. All nine smoke routes returned HTTP 200. SQLite inspection found masked authorization, Mock markers and matching correlation IDs in all five verification history rows. No ERR/FTL or unhandled exception entries were found in verification logs. No live CRPT request was made and no commit or publication was performed. The isolated Mock preview remains available at http://localhost:5287/operations with data under artifacts/unified-operations/App_Data.

## Context and Orientation

Run commands from `D:\Артём\Projects\C#\СZHELPER`. `src/CrptWorkbench.Web/Components/Layout/MainLayout.razor` owns sidebar navigation. `Components/Pages/ApiConsole.razor` in that project owns lookup forms, results and favorites. The registry in `src/CrptWorkbench.Application/Operations.cs` supplies four lookup descriptors. The three document pages call existing application services and must retain their behavior. Styling is shared in `src/CrptWorkbench.Web/wwwroot/app.css`. `tests/CrptWorkbench.Tests` uses xUnit and HtmlRenderer for presentation tests.

## Plan of Work

Create `src/CrptWorkbench.Web/Components/Pages/Operations.razor` at `/operations`, showing registry lookups plus package and correction links. Show the unimplemented aggregation tree separately as coming soon. Replace both old menu links with a single OperationsNavLink; give all working pages a return link and Operations eyebrow. Rename the console to Получение сведений, retain its system tabs and favorites, and add the home catalog button. Redirect `/special-features` to the catalog with replacement of browser history. Preserve all existing working URLs.

Add `src/CrptWorkbench.Web/OperationNavigation.cs` for console URLs, path membership and query selection. Unknown operation IDs show a Russian message and catalog link without a selected form. Query code takes precedence over operation. Ensure changing the query on an already displayed console selects the correct form. Extend safe page logging with the new catalog and aggregation paths.

Add focused tests for URL selection, unknown operations, encoded code compatibility and active navigation. Then perform browser checks in a separate local Mock app with data under ignored artifacts; do not stop user-owned servers or write to their data.

## Concrete Steps

The branch command is `git switch -c codex/unified-operations` (completed). The baseline command is `git diff --binary --output=artifacts/unified-operations/baseline.patch -- .` (completed before edits).

Run `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false` and `dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false`. Run `dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed` outside the sandbox from its first attempt. Require zero failed tests and document the actual count rather than relying on the old 307 baseline. If necessary restore with `dotnet restore CrptWorkbench.sln -p:RestoreUseStaticGraphEvaluation=true`.

Start the app on an unused localhost port with explicit Crpt:Mode=Mock and Crpt:DataDirectory under artifacts/unified-operations. Check `/`, `/operations`, `/console`, all three workflow pages, `/history` and `/settings` for HTTP 200 and clean logs. Use browser controls for interactions and capture screenshots under artifacts/unified-operations.

## Validation and Acceptance

All seven catalog cards must open their intended forms. Operation URLs must not execute a request on opening. Unknown IDs show a recovery link; existing code links from history still execute the lookup. The Operations sidebar item remains active on all related pages and inactive on history/settings. Favorites still persist per profile. Verify a Mock request with its marker, history entry and masked headers. Check the legacy redirect, both themes, visible keyboard focus and 380px layout without horizontal overflow. Save catalog and working-form screenshots and review server/client logs for errors.

## Idempotence and Recovery

Never reset or restore the dirty working tree wholesale. Compare files against the baseline patch when reviewing this task. Build outputs, screenshots and temporary Mock data stay ignored. Do not switch back to master while the feature is uncommitted, because branches alone do not isolate uncommitted files. Stop only the verification process started for this task.

## Artifacts and Notes

Initial diff: `artifacts/unified-operations/baseline.patch`. Final verification evidence and screenshots will be placed beside it. The existing six dirty files remain user-owned work; report them separately.

## Interfaces and Dependencies

Only Web-facing types are added: ConsoleDestination and OperationNavigation, plus OperationsNavLink inheriting Blazor NavLink. Application and Domain interfaces, client allowlists, schemas, signing and polling remain unchanged. No new library is needed.

Revision 2026-09-27: Initial implementation plan and recorded discoveries following branch creation and catalog implementation.

## Verification evidence

The initial development run omitted ASPNETCORE_ENVIRONMENT=Development, so static assets were not enabled. Restarting only the task-owned server with Development fixed the preview; no product change was necessary. The final app run is `dotnet run --project src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-build --no-launch-profile -- --urls http://localhost:5287 --Crpt:Mode=Mock --Crpt:DataDirectory=D:\Артём\Projects\C#\СZHELPER\artifacts\unified-operations\App_Data`, with ASPNETCORE_ENVIRONMENT=Development. Verification process session: 57272.

Release test summary: total 319, passed 319, failed 0, skipped 0. Web Debug and tests Release builds each reported zero warnings and errors. The test count increased by exactly the 12 new OperationNavigationTests cases.

`browser-checks.json` records all seven destinations and their active menu state. Catalog and console were checked in both themes at 380x760, and all three document pages at 380px. Page scroll width was 365px (the viewport reserves scrollbar width), within 380px. Keyboard focus had a solid visible outline. Screenshots include catalog-dark.png, catalog-light.png, request-dark.png and the corresponding mobile captures under artifacts/unified-operations.

`git apply --reverse --check --exclude=src/CrptWorkbench.Web/Components/Pages/Home.razor artifacts/unified-operations/baseline.patch` succeeded, confirming original edits remain in place. Home's prior removal of the console button is intentionally superseded by the requested catalog button; no other baseline behavior was changed. Directory.Build.props and the CodeCorrection code-behind and isolated CSS received no feature edits. PackageAggregation and CodeCorrection markup received only the planned heading/return link edits on top of the baseline.

Revision 2026-09-27: Marked completed after builds, tests, SQLite and browser verification; corrected the Git-root assumption with live evidence and documented preview startup recovery.