# Корректировка сведений о кодах

This living ExecPlan follows [../PLANS.md](../PLANS.md). Maintain Progress, Surprises & Discoveries, Decision Log and Outcomes & Retrospective throughout implementation.

## Purpose / Big Picture

Provide `/code-correction`: select a supported product group and one correction kind, enter shared new values, paste or upload UTF-8 codes, optionally check their eligibility, remove invalid rows, submit one signed CIS_INFORMATION_CHANGE document and track it durably. Mock is default; the user explicitly approved this document type in Production, but not live verification traffic.

## Progress

- [x] (2026-09-27) Read repository architecture, plan and role instructions; inspect shared document submission/tracking and code-info contracts.
- [x] (2026-09-27) User approved all supported groups, shared values only, current reference, default-on optional preflight, row reasons/removal, and Production support.
- [x] (2026-09-27) Retrieve official current correction/group/permit documentation into docs/crpt-reference/cis-information-change-current.txt.
- [x] (2026-09-27) Implement rules and correction application service with server-side preflight state.
- [x] (2026-09-27) Extend strict transport and shared durable tracking without breaking existing package documents.
- [x] (2026-09-27) Implement responsive page, navigation and document list.
- [x] (2026-09-27) Verified 307 automated tests, browser Mock workflow, masking, restart persistence and independent security review; updated AGENTS baseline.

## Surprises & Discoveries

The parent Git repository currently reports this whole project as untracked. Preserve all existing files and do not stage or commit. Current official live documentation is v733.0 dated 25.09.2026; search indexing previously reported v649.0. The saved live snapshot is authoritative for this implementation. Existing tracking uses PackageDocument and the physical PackageTransformationDocuments table, supports durable GUID, leases and 24-hour pause.

The Action model limit was 16, while variableExpiration requires 18 characters. Widened metadata to 32 in an additive migration; SQLite already stores unrestricted TEXT so no table rebuild is needed. Official dictionary confirms EMPTY is no special state. A direct development DLL launch needs --environment Development to load the development static-web-assets manifest.

## Decision Log

2026-09-27: Use one correction kind and shared values per document; 100,000 unique codes maximum, no implicit splitting. Checkbox is enabled by default. Preflight queries code info in batches of at most 1,000. Failed/missing/ambiguous responses block checked submission, with row reasons. Deleting rows retains other results; editing context/values/codes invalidates them. Validate server-side against a scoped snapshot and consume it upon submission. A disabled checkbox bypasses remote code checks but never local format/group validation.

2026-09-27: Reuse existing durable table and worker, add a correction operation enum value and neutral document metadata only if needed; keep existing numeric enum values and package APIs compatible. No destructive migration. A correction must never fall through to transformation operation/type selection.

2026-09-27: Check all constraints available from code-info replies, including parent type resolved through the same allowlisted endpoint when required. Do not infer correction authority from current ownership. Remote-only registry/NC/VetIS checks remain explicitly listed as server checks; no new read endpoints or silent approval of missing essential code facts.

## Context and Orientation

Domain holds CisInfo and client/repository ports. Application PackageDocumentService signs and sends documents and PackageDocumentTracker polls leased records. Clients PackageCreationRequest validates creation envelopes before authentication; MockTrueApiClient returns restart-stable identifiers. Infrastructure maps PackageDocument to the legacy physical table. Web pages call application services, never transport/signing/EF directly.

The saved reference contains exact supported groups and field restrictions. Correction kinds are vsd, productionDate, expirationDate, variableExpiration, weight, tnved, permitDocs, licences, emissionType, partNumber. Dates/weight/permits/licences generally require APPLIED or INTRODUCED; tnved INTRODUCED; vsd INTRODUCED/RETIRED/WRITTEN_OFF; emissionType EMITTED/APPLIED; partNumber APPLIED. Apply group, packaging, aggregation, special-state and emission restrictions from the snapshot. All fields omitted unless selected; numeric fields must be JSON numbers even where examples use strings.

## Milestones

First build Domain rules and Application request/validation/preflight contracts. Tests demonstrate group/field restrictions, response mapping and stale checks. Second extend submission/transport/tracking with strict CIS_INFORMATION_CHANGE authorization, Mock proof and regression tests. Third expose the flow in Blazor and verify default-on checks, reasons/removal, unchecked mode and durable tracking through browser and SQLite tests.

## Plan of Work

Add CodeCorrectionRules in Domain and CodeCorrectionService/contracts in Application. The service parses code lines without stripping meaningful characters, reports duplicates and input errors, validates shared values, produces exact JSON, performs batched code-info preflight with masked history and cancellation/progress, and permits checked submission only for the matching retained subset of a server-side successful snapshot. Reuse existing shared submission service through an explicit correction branch and shared document metadata.

Add CodeCorrection.razor and scoped styles. Use existing JS text streaming for large lists, pagination of 100 rows, clear progress/error/empty states, cancellation, and profile-change guards. Add navigation. Show one correction kind, shared fields, row status/reasons/removal, check action followed by send, and auto-refreshed document tracking. Preserve moderate UI density and right-aligned submit actions.

## Concrete Steps

From repository root run:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

The test runner must run outside the restricted Windows sandbox from the first attempt. Use isolated app data and Mock for runtime/browser verification. If a user-owned build is locked use an alternate OutputPath; never stop that app.

## Validation and Acceptance

All existing 234 tests plus focused new tests pass with zero failures. Exercise complete rule matrix, value boundaries, exact signed bytes, rejected transport combinations before auth, missing/reordered/duplicate replies, network/cancellation and stale preflight, subset removal, profile/mode isolation, unknown submission without retry, GUID cancellation persistence, restart tracking, lease safety and masked SQLite history. Browser: page and existing relevant routes HTTP 200, default checked, mixed valid/retired/not-found codes yield reasons, removal enables send, unchecked path skips preflight, Mock document completes and survives reload. Inspect logs for unhandled exceptions and untracked outputs for secrets/runtime artifacts.

## Idempotence and Recovery

Preserve existing database and legacy physical table. Never resend ambiguous creation automatically. Persist GUID despite UI cancellation, retain manual pause/resume. Mock performs no network calls. No real document is submitted as validation.

## Artifacts and Notes

Protocol evidence: docs/crpt-reference/cis-information-change-current.txt, official v733.0 dated 25.09.2026, retrieved 2026-09-27. Prior v515 section 4.1.18 pages 259–273 is comparison only.

## Interfaces and Dependencies

Use existing ITrueApiClient, ICertificateSignatureService, IPackageDocumentRepository, ISensitiveDataMasker, IApiRequestLogRepository and TimeProvider. New services use DI and CancellationToken; public correction DTOs are immutable records. No added framework or package required. Keep UI independent of transport and persistence.

## Outcomes & Retrospective

Initial full Release run: 288 passed, 0 failed, 0 skipped; build zero warnings/errors. Independent security review identified unknown-value fallbacks and trailing control characters; fixed and added targeted regression cases. Final Release suite: 307 passed, 0 failed, 0 skipped in 9.887 seconds. Both documented builds completed with zero warnings/errors. Independent security review passed after regression fixes.

Browser Mock QA at localhost:5097: mixed four-code list reports one eligible, one retired, one wrong group and one absent; removing failed rows enables send; sent GUID survives reload and reaches CHECKED_OK. A second unchecked submission performs no additional code-info call. SQLite confirms two CodeCorrection records Finished/CHECKED_OK, masked headers, Mock proof, nonempty correlation IDs and durations. Page widths 390 and 1366 have no document-level horizontal overflow. GET /, /console, /history, /settings, /code-correction and /special-features return 200. No unhandled exceptions found in QA server logs.

Revision 2026-09-27: implementation and local validation complete; archived with final 307-test baseline. Mock documents remain completed after server restart. Live signing/submission and native file-dialog upload were not exercised; production target-environment verification remains separate.
