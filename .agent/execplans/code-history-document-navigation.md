# Open documents from a code's history

This ExecPlan follows `.agent/PLANS.md` and is kept current during implementation.

## Purpose / Big Picture

The document rows shown under a code become controls that open API Console and perform the approved read-only document lookup. The user sees the actual result and the system that answered. When True API explicitly has no document and the identifier is a GUID, the workbench tries the existing SUZ receipt lookup.

## Progress

- [x] (2026-09-29) Inspect the current worktree and create local checkpoint commit `30203f8` before feature edits.
- [x] (2026-09-29) Add document navigation, source resolution, stable Mock GUIDs, and accessible interactive row controls.
- [x] (2026-09-29) Release tests passed 446/446. Browser verified an operation-button keyboard click, automatic True API lookup, and SUZ fallback on a narrow viewport; relevant routes returned HTTP 200.

## Surprises & Discoveries

- The code history contract supplies `docId` without a system marker or SUZ receipt ID. A True API miss is the only sound trigger for trying SUZ; the operation label is insufficient.
- Mock True API previously returned a generic package document for every GUID. Two explicit GUIDs now return an empty list so SUZ fallback and total absence are observable.
- `Directory.Build.props` changed independently from 1.16.2 to 1.17.0 after the checkpoint; this plan does not own that edit.

## Decision Log

- Decision: Route history documents to True API first and try SUZ only after HTTP 404 or a valid empty True API document list, with a GUID. Rationale: These are participant-visible True API events, and only an explicit miss justifies the second approved read. Date/Author: 2026-09-29 / Codex.
- Decision: Keep old `demo-code-*` Mock status aliases while new Mock history emits stable GUIDs. Rationale: Saved history must remain actionable. Date/Author: 2026-09-29 / Codex.

## Outcomes & Retrospective

The feature works in Mock: a code history row can be opened with the keyboard, the Console executes the selected document lookup, and an explicit True API miss causes the SUZ receipt lookup. The Release suite passed 446 tests with zero failures and skips. Live service behavior remains target-environment verification.

## Context and Orientation

`src/CrptWorkbench.Application/TrueApiCodeInfoHandler.cs` reads `/cises/history` and enriches each event with a True API document detail. `src/CrptWorkbench.Web/Components/Pages/CodeInfoResultView.razor` displays those events in API Console and saved History. `src/CrptWorkbench.Application/CodeHistoryDocumentLookup.cs` orchestrates existing status operations through `ExecuteApiOperationService`, which persists masked, profile-scoped records. The SUZ status operation reads receipt 2.0 by GUID. No new production endpoint is introduced.

## Plan of Work

The component validates the existing document ID before rendering buttons on operation and identifier. Selection contains the ID, source and product group and navigates to `/console` with encoded parameters. Console runs the lookup once using the active profile, selects the actual answering system, and presents the status or clear absence message. Existing status handlers and the executor own requests and history. Mock history emits stable GUIDs while status reads accept old aliases.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, run `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`, then `dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false`. Run `dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed` outside the restricted sandbox. Inspect only `СZHELPER` paths in the final Git diff.

## Validation and Acceptance

In Mock, query a code with document history, click a document operation or identifier, and observe an automatic status lookup in API Console. The new GUIDs show True API documents. `/console?document=22222222-0000-0000-0000-000000000001&source=true-api` reaches SUZ after a True API miss; the GUID ending `000000000006` yields a clear not-found-in-both message. Automated tests must cover successful direct lookup, fallback, no document, access/server failures without fallback, masked history and profile isolation. Check keyboard focus and 380-pixel layout. No real CRPT traffic is needed.

## Idempotence and Recovery

Repeated clicks start new read-only lookups only after the current one completes. The checkpoint commit remains available if this feature needs to be reverted; do not reset or discard unrelated work. The new URLs encode document IDs and never contain credentials.

## Artifacts and Notes

Checkpoint: `30203f8 Checkpoint current work before document navigation`. Web and Release test builds: 0 warnings, 0 errors. Release suite: 446 passed, 0 failed, 0 skipped. Browser: `/`, `/console`, `/history`, `/settings` all returned HTTP 200. A narrow browser viewport showed five GUID rows and keyboard-activated operation `Оплата` navigated to a populated True API document result; direct Mock GUID `22222222-0000-0000-0000-000000000001` selected SUZ after the True API miss.

Revision note, 2026-09-29: completion evidence and outcomes were added after the automated and browser checks.

## Interfaces and Dependencies

`CodeHistoryDocumentSelection` carries document ID, `ApiSystem` and optional product group. `CodeHistoryDocumentLookup.ExecuteAsync` accepts a selection and active profile ID, returns the selected system, persisted `ApiExecutionResult`, absent flag and whether both systems were tried. Web invokes this Application service, and the service invokes the already registered True API and SUZ operation handlers through `ExecuteApiOperationService`. Database schema and production transport allowlists are unchanged.
