# Validate workbench input

This ExecPlan is a living document maintained according to `.agent/PLANS.md`.

## Purpose / Big Picture

Users receive field-specific errors before invalid profile settings or operation inputs are saved or sent. The same checks apply to calls that bypass the UI.

## Progress

- [x] (2026-09-25) Inspected forms, handlers and the SUZ reference.
- [x] (2026-09-25) Added shared validation and TraceX syntax validation.
- [x] (2026-09-25) Displayed field errors in Settings and API Console with isolated editing.
- [x] (2026-09-25) Added focused tests; Release suite passed 138/138 and browser checks passed.

## Surprises & Discoveries

The SUZ reference uses UUID-shaped `omsId` values (section 4.4.1), but does not define `ConnectionId`. The latter must not be restricted to UUID without contract evidence.

## Decision Log

- Decision: Validate INN and GTIN format without check digits in every mode. Rationale: Explicit user instruction. Date/Author: 2026-09-25 Codex.
- Decision: Use UUID format for OmsId and a bounded printable identifier for ConnectionId. Rationale: Only OmsId has UUID evidence in the local SUZ reference. Date/Author: 2026-09-25 Codex.

## Outcomes & Retrospective

Profile and operation input is validated before persistence or client calls. Browser checks confirmed local field errors and a successful mock exchange; Release tests passed 138/138. Production endpoint behavior was not exercised live.

## Context and Orientation

`src/CrptWorkbench.Web/Components/Pages/Settings.razor` edits a `CrptProfile`, which `ProfileService` in `src/CrptWorkbench.Application/WorkbenchServices.cs` saves. API Console calls handlers through `ExecuteApiOperationService`. `src/CrptWorkbench.Infrastructure/TraceXConnectionTester.cs` owns SQL-provider code. UI must not own parsing or database connection logic.

## Plan of Work

Create field-named validation results in Domain and rules in Application. Check profile properties before persistence and operation inputs before handlers run, using handler parsers where available. Add a Domain port and Infrastructure implementation for local connection-string syntax checks. Edit a cloned profile in Settings and show errors beside fields. Mirror operation checks in API Console, leaving Application as the authority. Cover invalid boundaries, no side effects and wrong check digits in tests.

## Concrete Steps

From the repository root, run `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`. Build the tests in Release, then run the documented Release `dotnet test` outside the restricted sandbox. Start the web app and inspect `/`, `/console`, `/history`, `/settings` plus invalid and valid form submissions.

## Validation and Acceptance

Invalid fields show Russian messages beside the field. Invalid profile data never reaches persistence; invalid operation input never calls a client. Existing secrets are not replaced on validation failure. Mock examples still run. INN and GTIN with valid lengths and incorrect check digits are accepted.

## Idempotence and Recovery

No migration is required. Changes are source-only and can be rebuilt repeatedly. Existing profile rows are validated when edited or used, without automatic rewriting.

## Artifacts and Notes

Release suite: 138 passed, 0 failed. Browser: `/`, `/settings`, `/console`, `/history` loaded; invalid profile name and INN and invalid code showed field errors. A known mock code returned HTTP 200, appeared in history with `Bearer ********` and `X-CRPT-Workbench-Mock: true`. Server log showed no unhandled exceptions.

## Interfaces and Dependencies

Domain owns `InputValidationResult`, field issues, and `ITraceXConnectionStringValidator`. Application owns rules. Infrastructure implements connection-string validation using SQL Server and PostgreSQL builders without opening a connection. Web consumes validation results; dependency registration stays in Program.cs.

Revision note (2026-09-25): marked completed work and corrected the result type location after implementation; recorded verification evidence.
