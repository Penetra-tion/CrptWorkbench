# Organize operations by system and move favorites to the catalog

This ExecPlan is a living document maintained according to `.agent/PLANS.md`.

## Purpose / Big Picture

Users can find operations by CRPT system while keeping task categories, and reach favorites from one place at the top of `/operations`. Stars persist per profile, including package and correction workflows.

## Progress

- [x] (2026-09-28) Saved the previous working tree in commit `6c82f60`.
- [x] (2026-09-28) Added system tabs, task categories, favorite cards and persistence support.
- [x] (2026-09-28) Release build passed with zero warnings; 420 Release tests passed with zero failures. Browser showed True API/SUZ tabs and profile-scoped favorite persistence after reload; main routes returned HTTP 200.

## Surprises & Discoveries

- The existing favorites repository stores arbitrary operation IDs, but its application service filtered against the API registry; workflow IDs therefore need explicit recognition in that service.

## Decision Log

- Decision: Reuse the existing favorites repository with three stable workflow IDs and no migration. Rationale: It already scopes rows by profile and avoids a second collection. Date/Author: 2026-09-28 / Codex.

## Outcomes & Retrospective

The catalog and favorites behavior are implemented. Interactive inspection confirmed the SUZ tab and favorite persistence after reload. The final 380 px viewport check and interactive profile switching were not performed in this run.

## Context and Orientation

`src/CrptWorkbench.Web/Components/Pages/Operations.razor` renders the catalog. `src/CrptWorkbench.Application/Operations.cs` validates saved favorite IDs. `src/CrptWorkbench.Web/Components/Pages/ApiConsole.razor` previously owned the favorite picker. API operations come from `IApiOperationRegistry`; package and correction workflows have dedicated routes.

## Plan of Work

Save the existing work first. Build one catalog from the registry plus the three workflow routes. Render favorites above system tabs and task groups within each system. Move star controls to catalog cards, leaving the console's system and operation selection intact. Update CSS for the separate link and button. Verify that favorite reads are versioned across profile changes.

## Concrete Steps

From the repository root run `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`, then build and run the Release test project with the commands in `AGENTS.md`. Start the web app and inspect `/operations`, `/console`, `/history`, and `/settings` in a browser, including 380 px width.

## Validation and Acceptance

The catalog opens on True API, shows favorite cards across systems, hides empty categories, and persists stars under the active profile. Clicking a card opens its form without running it. The console still selects operations and accepts code links. Build and tests pass with zero failures.

## Idempotence and Recovery

The database is unchanged. Repeating the code change or deployment does not migrate data. The preliminary commit preserves the pre-change state.

## Artifacts and Notes

Commit `6c82f60` is the local baseline for review.

## Interfaces and Dependencies

`FavoriteApiOperationService` accepts registry IDs and the three workflow IDs. `Operations.razor` uses it with the existing `WorkbenchContext` profile. No client or transport contracts change.
