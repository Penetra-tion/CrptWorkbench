# Формирование упаковок и общее отслеживание документов

This ExecPlan is maintained under `.agent/PLANS.md`. It implements the user-approved plan of 2026-09-27.

## Purpose / Big Picture

An operator opens `/package-aggregation`, chooses a product group and BOX (КИТУ) or GROUP (КИГУ), supplies existing item/package code pairs, previews each package, and submits one document per package. Both Mock and Production use the existing True API port. Each GUID is persisted and tracked across restarts for 24 hours; manual resumption starts another window. No code generation, label printing, remote preflight, or dependent multi-level creation is included.

## Progress

- [x] (2026-09-27) Read existing transformation implementation, project instructions, and True API v515 sections 4.1.1 and 4.1.2.1 (pages 55-64).
- [x] (2026-09-27) Implemented common submission/tracking persistence and aggregation validation.
- [x] (2026-09-27) Implemented tightly scoped transport support and the new UI.
- [x] (2026-09-27) Release suite: 228 passed, zero failed/skipped. Browser BOX/GROUP success/error scenarios and restart persistence verified.
- [x] (2026-09-27) Updated AGENTS.md production boundary, workflow and baseline.

## Surprises & Discoveries

The current production client validates the creation URL but does not bind document type to operation. This must be enforced for both approved package operations. The existing database table can retain its physical name while its CLR model becomes common, avoiding destructive table recreation. The workspace is entirely untracked in the parent Git repository; preserve all existing files and inspect only this project.

## Decision Log

2026-09-27: Preserve `PackageTransformationDocuments` as the database table name and add `OperationKind` (default Transformation) and nullable `PackageType`. Rename the CLR entity/repository/tracker to PackageDocument terminology. This retains old GUIDs, timing, and states while serving both pages.

2026-09-27: Use a shared PackageDocumentService for submission, list filtering and tracking controls. Specific transformation/aggregation services construct their respective document bodies. Keep one document per parent, one level per submission, local validation only, and no automatic replay of creation after an uncertain outcome.

## Outcomes & Retrospective

Implemented both package operations on common durable storage and polling. Release build passed with zero warnings/errors; all 228 tests passed outside the restricted sandbox. An isolated Mock server returned HTTP 200 for /, /console, /history, /settings, /special-features and both package pages. Browser submissions created distinct GUIDs for two BOX and two GROUP packages, including one deterministic CHECKED_NOT_OK result. Restart retained those GUIDs and results. Invalid multi-level input displayed a line error and disabled submission. Real CRPT traffic and certificate signing were not performed.

## Context and Orientation

Domain defines PackageDocument and IPackageDocumentRepository. Application defines operation services, the common submission service and PackageDocumentTracker. Infrastructure maps the common entity to the existing table and adds a reviewed migration. Web registers these services and runs one PackageDocumentWorker. Razor consumes Application only. Clients maintains the existing production hosts, authentication, detached document signatures, and mock guard.

## Plan of Work

Introduce PackageOperationKind.Transformation/Aggregation and add OperationKind and PackageType to the common persisted record. Preserve all old columns and physical table/index names. Filter lists and manual controls by profile and operation. Reuse the current 5/15/30/60/300-second schedule, 24-hour window and lease claiming. Persist creation intent before sending and final state with cancellation-independent persistence after an HTTP outcome. Treat uncertain HTTP/server failures conservatively as Unknown; never replay them.

PackageAggregationService exposes Parse(string), SubmitAsync(Guid profileId, string group, string packageType, IReadOnlyList<TransformationRow> rows, CancellationToken), ListAsync, ResumeAsync and CheckNowAsync. Parse returns TransformationPreview with Rows, Errors, PackageCount. Validate UTF-8 size, row count, allowed characters and lengths, duplicate children, self links, forbidden aggregate masks and any parent appearing as a child. PackageType accepts BOX or GROUP. Use the same reviewed 34 product groups as transformation. Build participantId and aggregationUnits containing exactly one unitSerialNumber, aggregationType AGGREGATION and string-array sntins. Outer type is AGGREGATION_DOCUMENT, format MANUAL. Package type is local metadata only.

The new Razor page uses these methods plus PackageDocumentTracker.PollDueAsync and shared favorites. It groups preview by parent with expandable contents and shows tracking rows for aggregation only. Existing transformation UI switches its record/tracker types but retains behavior. Navigation exposes both operations.

Production must validate the exact operation, method, path, supported product group, MANUAL envelope and matching document type before authentication. Mock must create unique GUIDs and return matching document metadata with the mock proof header; include deterministic error coverage. Retain signed-body suppression in logs and persist only masked technical history.

## Concrete Steps

Work from `D:\Артём\Projects\C#\СZHELPER`. Build with `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`. Build tests with `dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false`. Run `dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed` outside the restricted sandbox with execution approval. Expect zero failed tests; record the new count in AGENTS.md.

## Validation and Acceptance

Test parser limits and invalid graphs, BOX/GROUP payloads and exact signed UTF-8 bytes, allowed transport and rejected wrong types/groups/methods, ambiguous creation without replay, terminal errors, timing, mode changes, manual resumption, profile/operation isolation and migration from the previous schema preserving old records. Start an isolated Mock instance using temporary data, check HTTP 200 for home/console/history/settings and both package pages, submit multiple BOX/GROUP packages in a browser, observe separate GUIDs and terminal results, restart and confirm persistence. Inspect logs for unhandled exceptions and persisted history for masked headers. No real requests or certificate operations are permitted during verification.

## Idempotence and Recovery

Migration is additive and must not delete user data. Existing Sending records remain visibly uncertain and are never re-sent. Read-only status polling may retry; expired leases recover after restart. Preserve all runtime databases, keys and user-owned processes. Only stop the isolated verification process started for this task.

## Interfaces and Dependencies

Use existing ITrueApiClient, ICertificateSignatureService, IProfileRepository, IApiRequestLogRepository and ISensitiveDataMasker. Keep Domain independent; Application and Clients reference Domain only. Use immutable aggregation DTOs. No new packages or production endpoints are required.

IPackageDocumentRepository.ListAsync filters by profile and PackageOperationKind before taking the latest 200 rows. ScheduleAsync(profileId, id, kind, nowUnixMs, resume, cancellationToken) changes only scheduling fields with profile/kind/state predicates. CompleteCheckAsync(document, expectedLeaseUntilUnixMs, cancellationToken) saves only polling fields while State remains Tracking and the stored lease matches, returning false for an obsolete worker. The tracker claims one record immediately before each request, up to ten per iteration.

## Artifacts and Notes

Contract evidence: the JSON example on PDF page 60 uses string-array sntins; pages 61-64 define BOX/GROUP and prohibited masks. Do not infer a package-type wire field from the local UI selection.

Final evidence: documented Debug Web and Release test builds both reported zero errors and warnings. The outside-sandbox Release runner reported 228 passed, 0 failed, 0 skipped. Five browser-created documents and ten exchanges were verified in the isolated SQLite database; every exchange has matching correlation headers and the Mock proof header, with Bearer/signature masked. The history UI displays the creation request and returned GUID; the transformation list remains separate. The server/client logs contain no unhandled errors. Screenshot: `artifacts/verification/package-aggregation/result.png`. Verification data and screenshots remain in ignored artifacts; no generated databases, keys, bin/obj or artifacts appear in the scoped untracked source list.

2026-09-27: Created the implementation plan from the approved design and current source inspection.

2026-09-27 implementation revision: Added masked technical history for both creation and polling; package creation transport headers are masked even under full logging. Independent review found a stale-lease race in the reused tracker. Polling now claims one item immediately before checking, repeats the due predicate atomically and saves through CompleteCheckAsync only if the lease still matches. Manual scheduling updates individual fields atomically. A regression test proves that an expired worker cannot overwrite a newer terminal result. The first suite run exposed only two obsolete fixed migration-count assertions; these now compare the applied and available migration lists.
