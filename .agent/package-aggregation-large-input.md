# Simplify package formation and support sets and large input

Maintain this ExecPlan according to `.agent/PLANS.md`.

## Purpose / Big Picture

At /package-aggregation the operator uploads or pastes any number of rows, submits normal КИТУ/КИГУ without selecting their type, or selects «Набор (КИН)» for set formation. Remove the duplicate mode badge in preparation. Keep actual Mock/Production configuration and historical mode labels.

## Progress

- [x] 2026-09-27: Inspected existing implementation and True API v515 pages 60–64 and 72–75; user confirmed separate set checkbox.
- [x] Removed aggregation row/byte restrictions in the shared parser via an internal flag preserving transformation defaults; removed preparation badge.
- [x] 2026-09-27: Implemented set contract and exact transport allowlist, input streaming and bounded preview.
- [x] 2026-09-27: Builds and initial 234 tests passed; isolated Mock browser exercised 20,001-row paste, submission, set submission, pagination, edit invalidation, group preservation and restart persistence. Guidance updated.
- [x] 2026-09-27: Final Web and Release test builds: zero warnings/errors. Final outside-sandbox test run: 234 passed, 0 failed, 0 skipped. Isolated server stopped; user-owned app preserved. Scoped untracked-file inspection excludes build outputs, keys, runtime databases and artifacts.

## Surprises & Discoveries

BOX/GROUP selection is only local metadata, never a wire field. Normal aggregation lets CRPT resolve the code type. Set JSON uses participantId and aggregationUnits with unitSerialNumber and sntins, omitting aggregationType. The existing textarea sends every full input event over SignalR, whose message limit also prevents large pastes.

## Decision Log

User confirmed automatic КИТУ/КИГУ and separate КИН on 2026-09-27. Use AUTO local metadata without guessing type from code length. Keep legacy BOX/GROUP service callers valid. SET metadata selects SETS_AGGREGATION at the existing endpoint, only for the 21 groups listed on PDF page 72. No new network endpoint or preflight is needed. Remove aggregation-specific row and byte ceilings; stream browser text and file content and paginate preview. Transformation limits remain unchanged.

## Outcomes & Retrospective

Browser successfully submitted 20,001 rows (one package) and one SET document; both reached CHECKED_OK and survived restart. Previewed 20,001 rows across 101 packages and advanced page 1 to 2; editing invalidated the preview. All five relevant HTTP routes returned 200. Four SQLite exchanges have masked Bearer/signatures, matching correlation IDs, durations and Mock proof. File chooser automation timed out twice in IAB, so large TXT selection was not browser-verified. Large paste and parser paths were verified. Real signing and live CRPT submission are outside local verification.

## Context and Orientation

Application PackageAggregationService parses rows and builds payloads; PackageDocumentService creates and persists one document per parent and shared tracking polls status. Clients PackageCreationRequest validates operation/envelope before authentication, MockClients generates restart-stable document metadata. Web PackageAggregation.razor owns input and preview. Persistence already has nullable string PackageType; no migration required.

## Plan of Work

Add shared Domain rules for supported set groups, use them in Application and Clients. Accept AUTO and SET in addition to legacy BOX/GROUP, omit aggregationType only for SET, choose correct envelope type using persisted metadata. Extend mock identifier marker and type reconstruction for sets. Replace selector with checkbox and filter group choices. Read textarea via IJSStreamReference only on preview; use a small JS callback to invalidate stale preview. Keep browser text local rather than echoing it in render diffs. Paginate package and child lists. Test >20k rows and >1MB, set payload and allowed/disallowed groups, signed bytes, transport rejection before auth and restart metadata.

## Concrete Steps

From repository root run `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`; then `dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false`. Run `dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed` outside sandbox with approval. Use isolated app data under ignored artifacts for Mock browser verification.

## Validation and Acceptance

Require zero build errors and no new warnings, all tests passing. Browser must preview a >20k-row paste/upload with bounded visible rows, invalidate preview on edit, switch set checkbox and submit Mock documents with GUID and tracked status. Check relevant HTTP routes and server errors. Confirm history masking and Mock proof headers through existing tests and inspected isolated data. Update dated test count in AGENTS.md.

## Idempotence and Recovery

Preserve existing user data, processes and prior source. Never resend uncertain creation results. Stop only the verification app created here. No database migration or destructive operation is required.

## Interfaces and Dependencies

Retain public string-based SubmitAsync compatibility. Add only Domain set-group rule and nullable aggregationType serialization. Use existing IJSStreamReference, InputFile, shared document persistence/signature ports. No new dependencies.

## Artifacts and Notes

Reference: True API v515 section 4.1.2.1 pp.60–64 and 4.1.2.4 pp.72–75. No row ceiling appears in these contracts. 2026-09-27 initial plan records user-approved set scope and preliminary limit edits.

2026-09-27 verification revision: Corrected JS stream return to a Blob (Blazor creates the stream reference), keyed the filtered select to preserve selected group, and scoped the aggregate-mask prohibition to ordinary aggregation per PDF p.61. Web build uses bin/aggregation-update because user-owned Debug assemblies are locked. The isolated app needs Development for static assets. Screenshot: artifacts/verification/aggregation-update.png.

2026-09-27 completion: All requested changes implemented. File chooser automation and live CRPT verification remain the explicitly documented limits; no real network submission occurred. Existing running user app must be rebuilt/restarted to display the source changes.
