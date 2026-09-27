# Make request history understandable

This ExecPlan is a living document maintained under `.agent/PLANS.md`.

## Purpose / Big Picture

An operator can open a recent request, read its saved result in the same friendly cards as API Console, and switch to the recorded HTTP exchanges when needed. Human names replace internal operation IDs. No request is replayed.

## Progress

- [x] (2026-09-25) Inspected history, result views, operation descriptors, and True API document types.
- [x] (2026-09-25) Added friendly names, document type labels, saved result projection, and deep-link navigation.
- [x] (2026-09-25) Release build passed with 0 warnings; 141 tests passed; relevant pages returned HTTP 200 in Mock mode.

## Surprises & Discoveries

- History already has `GetAsync(Guid)` and stores compound exchanges in `ExchangesJson`, so no persistence migration is needed.
- True API PDF section 4.1 calls `REAGGREGATION_DOCUMENT` "Трансформация упаковки".

## Decision Log

- Decision: Reuse the existing result-view components and parse persisted response bodies in a shared application helper. Rationale: old rows become readable without data migration. Date/Author: 2026-09-25, Codex.
- Decision: Use `?id=<Guid>` for selected history; navigation changes the address. Rationale: supports direct links, reload, and browser Back. Date/Author: 2026-09-25, Codex.

## Outcomes & Retrospective

Friendly history, direct links, saved-response rendering, and document labels are implemented. Release build completed with zero warnings and errors; 141 tests passed with zero failures. The local app returned HTTP 200 for `/`, `/console`, `/history`, `/settings`, and a missing history ID; server logs showed no unhandled exception. The local history was empty, so an existing-record click and browser Back were validated through the implemented URL flow and stored-response tests rather than a populated live UI session.

## Context and Orientation

`src/CrptWorkbench.Web/Components/Pages/Home.razor` lists five recent logs. `History.razor` lists 100 logs and shows technical details. `ApiConsole.razor` hosts the friendly result components. Application descriptors supply operation names; `DocumentStatusResults.Type` supplies document labels.

## Plan of Work

Update the four operation descriptor names. Add a shared application projection for stored responses, including malformed data and SUZ fields. Render the projection in History with the existing result components. Keep the old technical exchange display behind a switch. Link Home rows to History IDs; resolve IDs with `HistoryService.GetAsync`, retain selections through refresh, and respond to navigation changes. Expand document labels from section 4.1 of `docs/crpt-reference/true-api-gis-mt-v515-2025-06-04.pdf`.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, build with `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`. Build Release tests, then run the documented Release test command outside the restricted sandbox. Start the app in Mock mode and inspect `/`, `/console`, `/history`, and `/settings`.

## Validation and Acceptance

Recent rows link to a selected history entry and show readable operation/system names. Known stored response shapes show cards; unsupported shapes show a notice with a technical-view option. Browser Back restores the prior selection. Document type `REAGGREGATION_DOCUMENT` displays “Трансформация упаковки”. All tests pass; no real CRPT network call is made.

## Idempotence and Recovery

All UI changes are additive. No database or external state is changed. Rebuilds and test runs may be repeated; revert the scoped source diff if needed.

## Artifacts and Notes

The existing mock response header `X-CRPT-Workbench-Mock: true` proves local operation execution. The new `SavedResultViewTests` cover old saved response bodies for code, catalog, document and SUZ operations, plus damaged data and document labels. The parent Git tree currently reports this entire `СZHELPER` directory as untracked, so a normal scoped Git diff is unavailable; files were inspected directly.

## Interfaces and Dependencies

The application-layer projection accepts an `ApiRequestLog` and parses its stored `ResponseBody`; Razor only renders the resulting typed model. The UI uses the existing `HistoryService.GetAsync(Guid)` for links. No new external dependency.
