# Add True API document status lookup

This ExecPlan is a living document maintained under `.agent/PLANS.md`.

## Purpose / Big Picture

An operator can enter one document ID in API Console and see when True API received it, its type, product groups, processing status, and errors. Mock mode demonstrates the same workflow locally; Production uses the existing True API token and a narrowly allowed read request.

## Progress

- [x] (2026-09-23) Reviewed the True API PDF §6.4, pages 499–505, and current operation, transport, and console code.
- [x] (2026-09-23) Added DTO, parser, operation handler, mock examples, production URL validation, and registration.
- [x] (2026-09-23) Added a readable result component and operation-aware console integration.
- [x] (2026-09-23) Added focused contract, guard, retry, parsing, cancellation, and persistence tests; updated documentation and the 121-test baseline.
- [x] (2026-09-23) Built Debug Web and Release tests with zero warnings, passed all 121 Release tests outside the sandbox, exercised Mock HTTP pages, and inspected scoped files.

## Surprises & Discoveries

- The PDF's separate `/doc/validator/status` method is only for preliminary EDI checks and does not return upload time, type, or product group. §6.4 `/api/v4/true-api/doc/{docId}/info` returns all requested fields.

## Decision Log

- Decision: Use §6.4 document info with `body=false` and `content=false`, rather than validator status.
  Rationale: It returns the five requested fields without downloading document content.
  Date/Author: 2026-09-23 Codex.
- Decision: Keep one document per operation and optional `pg`.
  Rationale: User selected single-document lookup; §6.4 only requires `pg` for natural fur.
  Date/Author: 2026-09-23 Codex.
- Decision: Reject encoded controls and separators in the client and network guard as well as in UI input.
  Rationale: A direct call to the client or guard must not bypass path validation.
  Date/Author: 2026-09-23 Codex.

## Outcomes & Retrospective

The console now exposes one-document lookup with the five requested fields and error details. Mock examples cover success, pending, processing error, and missing ID; Production reuses the True API token with the new v4 read allowlist. The local server returned HTTP 200 for `/`, `/console`, `/history`, and `/settings` without unhandled exceptions. Live True API validation remains dependent on a server certificate and a real document ID.

## Context and Orientation

`src/CrptWorkbench.Application` owns API Console operation handlers; `src/CrptWorkbench.Clients` owns mock/real transports and network guards; `src/CrptWorkbench.Web/Components/Pages/ApiConsole.razor` renders operation input and results. `ITrueApiClient` is the existing transport port in Domain. The v3 base URL remains in `CrptIntegrationOptions`; document info is a v4 request on the same approved host.

## Plan of Work

Add document request/response records and a parser in Application, plus `TrueApiDocumentStatusHandler`. Build a safe document info URL in Clients and accept only this operation's GET in the real client and guard. Add deterministic mock responses. Register the handler and render parsed results in a dedicated component, with all fields and friendly labels. Add focused tests and update the documented production boundary.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, edit the Application, Clients, and Web files described above. Then run `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`, build Release tests, and execute the documented Release test command outside the restricted sandbox. Start the app in Mock and load `/`, `/console`, `/history`, and `/settings`.

## Validation and Acceptance

For a successful demo ID, the console displays time, type, group, status, and no errors. For a failed demo ID, it displays the distinct error texts. An unknown ID displays document not found. Tests prove URL encoding, query boundaries, 401 retry, mask and history handling, and that mock exchange carries `X-CRPT-Workbench-Mock: true`. Expect zero build errors and zero failed tests. Production connectivity requires a real certificate and document ID and is not established by local tests.

## Idempotence and Recovery

Changes are additive and require no migration. Mock tests have no external effects. Preserve generated runtime data and unrelated parent-repository files.

## Artifacts and Notes

The contract is `GET /api/v4/true-api/doc/{docId}/info` with `body=false`, `content=false`, optional `pg`. The JSON response is an array with `receivedAt`, `type`, `productGroup`, `status`, `errors`, and `commonErrors[].errorMessage` (PDF §6.4, pages 499–505).

Verification: Debug Web build and Release test build each completed with 0 warnings/0 errors. Release run: 121 passed, 0 failed. Mock server logs recorded GET `/`, `/console`, `/history`, `/settings` as 200 with no unhandled exception.

## Interfaces and Dependencies

Keep `ITrueApiClient.ExecuteAsync(IntegrationRequest, CancellationToken)` unchanged. Register the new `IApiOperationHandler` in Web. Use `System.Text.Json` and existing result/history/masking services; do not add dependencies.

Revision 2026-09-23: recorded implemented behavior and final verification after completing the feature; added encoded-ID validation and reran 121 tests.
