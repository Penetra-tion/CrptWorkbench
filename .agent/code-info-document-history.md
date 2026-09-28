# Show document history and payment for a code

This ExecPlan is a living document governed by `.agent/PLANS.md`.

## Purpose / Big Picture

The code lookup should show the documents associated with each found code and a separate payment answer. A user can submit a known Mock code in API Console, see the document operations and payment state, and reopen the same result from History. Production uses only the explicitly authorized True API code history method and the already allowed read-only document information method.

## Progress

- [x] (2026-09-28 01:04 UTC) Reviewed the existing lookup, mock and production guards, history storage, and True API v733 sections 5.2 and document information.
- [x] (2026-09-28 01:13 UTC) Added safe history/document enrichment, canonical production guard, and local Mock examples.
- [x] (2026-09-28 01:16 UTC) Added document table and separate payment field to the shared result card, including saved results.
- [x] (2026-09-28 01:20 UTC) Added focused contract, failure, guard and rendering tests. Release suite: 398 passed, 0 failed. Web build: zero warnings/errors. Mock app returned HTTP 200 for `/`, `/console`, `/history` and `/settings`.

## Surprises & Discoveries

- True API section 5.2 `/cises/history` returns a list of code events with `docId` and timestamps, but not a document type. Type and participant details require the existing `/doc/{id}/info?body=false&content=false` read.
- A history response only includes events visible to the authenticated participant. The payment answer must describe the visible document history, not claim a global billing record.
- The working tree has extensive pre-existing changes; preserve all unrelated edits.

## Decision Log

- Decision: Attach document history to each `CodeInfoItem` in the existing operation result. Rationale: the card and saved history use the same serialized result, so both show the enrichment without a new page or database change. Date/Author: 2026-09-28 Codex.
- Decision: `Да` means a visible document whose type is `PAYMENT`; `Нет` means a successfully read history with no such document; failed history/document detail reads produce `Не удалось проверить`. Rationale: follows the user's rule while avoiding a false negative when the API fails. Date/Author: 2026-09-28 Codex.

## Outcomes & Retrospective

The lookup now saves a per-code document table and payment answer with its masked technical sub-exchanges. The same card renders current and reopened results. Mock verifies paid, unpaid, and failed-history cases. Production transport is allowlisted for only the explicitly requested history query. Live CRPT document types and completeness still require verification in the target environment; no live request was made.

## Context and Orientation

`TrueApiCodeInfoHandler` in Application currently makes one `/cises/info` call and returns an array of `CodeInfoItem` from Domain. `MockTrueApiClient` and `RealTrueApiClient` in Clients implement the shared `ITrueApiClient` port. `ProductionTrueApiNetworkGuardHandler` is the final host/method/path safeguard. `CodeInfoResultView.razor` renders current and saved arrays. The execution service masks and stores the full result and optional sub-exchanges in SQLite history.

## Plan of Work

Add optional history fields to the Domain result. After successful code information lookup, request `/cises/history?cis=` for each distinct found code, then fetch unique document IDs through the existing read-only info method. Preserve response order and duplicates. Record all sub-exchanges so technical history remains honest. Translate document types for display, preserve raw types, and show sender/receiver only when returned. Keep unknown or failed enrichment visible without hiding the successful code information.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, build with `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`. Build tests in Release, then run the documented Release test command outside the restricted sandbox. Start the app in Mock mode and inspect `/`, `/console`, `/history`, `/settings` plus the enriched code result. Diff only paths under `СZHELPER`.

## Validation and Acceptance

The Mock paid code shows a payment document and `Да`; the Mock unpaid code shows no payment document and `Нет`. Both show operation, time, ID, and available participants. A failed history read says it cannot check. No request goes to a real host in Mock. Production permits exactly POST `/api/v3/true-api/cises/history?cis=<one canonical code>` on the reviewed host; query injection and other paths are rejected. All Release tests pass with zero failures.

## Idempotence and Recovery

Changes are additive to DTOs and transport allowlists. No migration or destructive action is needed. Re-running lookup requests fresh visible history; saved results remain readable because optional fields are nullable.

## Artifacts and Notes

True API v733 section 5.2 describes POST `/cises/history`, one normalized code per request, `docId`, and event timestamps. The document info contract describes GET `/doc/{docId}/info` with `body=false&content=false` and document type/participant metadata.

## Interfaces and Dependencies

Preserve `ITrueApiClient.ExecuteAsync(IntegrationRequest, CancellationToken)`. Domain owns serializable code/document records. Application coordinates requests; Clients own mock and allowlist behavior; Razor only renders the returned data.

Plan created after contract and repository review so this multi-layer feature remains reviewable.

Plan updated after implementation to record the exact Release result and the remaining live-environment verification boundary.
