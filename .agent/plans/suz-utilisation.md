# Нанесение кодов через СУЗ

This living ExecPlan follows `.agent/PLANS.md`. Maintain Progress, Surprises & Discoveries, Decision Log and Outcomes & Retrospective throughout execution.

## Purpose / Big Picture

The operator can open `/suz-utilisation`, choose a product group, package type and report scenario, load full marking codes, supply common and per-code attributes and submit reports in Mock or Production. Favorites share the existing profile collection. Each report contains at most 30000 codes and is tracked durably through report info and receipts. Development verification never submits real documents.

## Progress

- [x] Current project state committed before implementation: `8b1b0ade99d397dabca3ae53cee26d76e512a6ee` (84 files; no runtime secrets or generated files).
- [x] Read repository guidance, PLANS.md and SUBAGENTS.md; inspect SUZ contracts and reference sections.
- [x] Implement all common-SUZ product group rules, full-code parser and input/TSV preparation.
- [x] Implement durable jobs/reports, additive migration, submission and leased tracking.
- [x] Implement exact production transport and local Mock exchanges.
- [x] Implement form, favorite groups, imports and incremental result lists.
- [x] Build, test, independently review and exercise Mock browser workflows.
- [x] Record verification and schedule shutdown only after successful completion: 03.10.2026 01:24:16 Moscow (+03:00); `shutdown.exe /s /t 300` succeeded; cancel with `shutdown /a`.

## Surprises & Discoveries

2026-10-03: Release build succeeds without warnings. First full suite: 626 tests, 589 passed and 37 failed; all failures originate in the new snapshot mixing typed entities with the existing string-based model. Fix and rerun before acceptance. Startup is blocked by the same model issue. Independent reviews also found nested receipt precedence, legal typed attributes, temporary full-code storage and stale profile UI updates; these are corrected. The PDF's furslp production-date conditional note is incomplete; UNIT conservatively requires the date, SET treats it as optional.

Full codes including ASCII GS and cryptographic suffix are required. Existing MarkingCodeInput strips at the first GS and TraceXImportService calls it; the new import must explicitly preserve full codes while leaving existing callers unchanged. SUZ report status requires GET `/api/v3/report/info`; report creation returns reportId, not a True API document ID. Some groups receive automatic application reports and must not permit manual creation. Milk UNIT requires dates while GROUP forbids them; variable expiry requires one GTIN. The PDF contains 43 attribute subsections, including pharma which is excluded.

## Decision Log

2026-10-03: Full-code TraceX previews use transient seekable memory buffers, with no plaintext crypto-tail temporary file. New jobs persist only Data Protection-protected payloads. A lifetime exclusive instance lock protects one data directory from concurrent startup recovery. UI imports recheck profile context after JavaScript awaits; special scenarios expose a code-only TSV template.

2026-10-02 user approved general SUZ Mock + Production, all general-SUZ groups and special report types, common fields plus per-code table, automatic splitting at 30000 and one package type selected per upload. Drugs/MDLP are excluded. Existing automatic groups are informational. No True API preflight is added. Full-code cryptographic values are retained only in protected operational payload storage, never logs/history. Default mode remains Mock. User authorized an initial project-only commit and shutdown 5 minutes after successful completion; never force-close applications and never schedule before mandatory checks pass.

## Outcomes & Retrospective

Implemented version 1.26.0. Documented Debug Web and Release test builds succeed with zero warnings/errors. Full Release suite outside sandbox passes 632/632, zero failed/skipped (final run 18.895 seconds). The snapshot conflict was fixed and all 37 initial failures disappeared. A subsequent build was blocked by the owned verification apphost; stopping only those two verification processes allowed the documented build to pass cleanly.

Mock browser checks passed: two GTIN-partitioned reports received separate GUIDs and successful RNMS receipts; row portions expanded 100→200 using Enter; UTF-8 TSV uploaded 101 rows preserving GS and individual weight, showing 100 rows and blocking missing dates; favorites synchronized with package aggregation and filter survived reload; switching to an empty second profile hid jobs; restart retained the completed job and both receipts; 380px screenshots showed contained fields and wrapping. Browser controls timed out for a 30001-code paste, so those three numeric boundaries rely on automated tests. No live TraceX connection or real CRPT submission was exercised.

HTTP checks `/`, `/console`, `/history`, `/settings`, `/operations`, `/suz-utilisation` all returned 200. There are no unhandled server/circuit exceptions; an initial antiforgery-key mismatch between separate isolated data directories was resolved by using the same browser-verification data directory. SQLite readback confirmed two Finished/SUCCESS reports, protected payloads, four safe history records with correlation IDs and no crypto suffix. Independent static security review passed after remediation; UI review findings were corrected. Final source diff excludes runtime databases, keys, logs and outputs. Live CRPT, live TraceX and target-server signing remain target-environment verification.

## Context and Orientation

Domain contains persistence models and ports. Application owns matrix, validation/preparation, submission and tracking use cases. Clients owns ISuzClient adapters and request guards. Infrastructure owns SQLite migrations, protected storage and repository concurrency. Web owns DI, hosted tracker and Razor UI. Existing ProductionSuz.cs provides separate Auto/Manual SUZ authentication and guarded receipt GET. Existing package tracker supplies lease/backoff patterns but its physical package table and True API document semantics must not be reused for SUZ.

## Milestones

First implement input/rules and shared Domain persistence contracts, then durable backend and exact client behavior. Verify parser and transport with focused tests. Next implement the form using those services, incremental lists and TraceX full-code import. Finally build and run the full Release suite and exercise the workflow in an isolated Mock instance, fix review defects and record evidence.

## Plan of Work

Add SuzUtilisation models/ports and Application rules with a reviewed matrix sourced from `docs/crpt-reference/suz-cloud-api-3.0.pdf` sections 2.3.1, 4.4.11 (pages 237-365), 4.4.13 (373-376), 4.4.18, 5.3 and templates. Copy only protocol facts into a masked text reference. Input accepts UTF-8 one-code-per-line or TSV header `code` plus available individual fields. Preserve GS and suffix, deduplicate exact codes and reject conflicting duplicate individual values. Group/package/scenario changes reset incompatible attributes. Provide only approved individual AI fields per template, with conflicts blocked rather than overwritten.

Preparation validates all rows before side effects and splits by compatible common attributes/GTIN and 30000. Persist a job and report drafts before POST. Store draft JSON through Data Protection, protect cryptographic tails at rest and never copy full request payload into diagnostic history. Persist Sending atomically before POST; only one submission claimant can send a Prepared part. After restart Sending becomes Unknown, never Prepared. POST failures stop later parts; only Prepared parts can be continued. Persist GUIDs with CancellationToken.None once received. Track with renewable ownership checks, mode/profile/connection isolation, five-second first check, then 15/30/60 seconds and five minutes after an hour, and a 24-hour tracking window. Track report status and result receipt separately; receipt failures must not turn accepted reports into failures. Terminal errors and successful receipt distinguish completion. Pause on changed mode/connection and permit explicit resume/check. History stores counts, IDs and statuses without codes, tokens or signatures.

Extend ProductionSuz guard only with POST utilisation (omsId query only, reviewed group/body shape and required detached signature) and GET report/info (omsId/reportId GUIDs only). Validate operation/method/path and body before authentication. Auto reads can refresh once on 401; Manual never refreshes; POST never retries. Sign exactly the JSON UTF-8 body using existing CryptoPro detached CMS and add X-Signature, clientToken and correlation. Send no redirects. All exchange headers and request bodies are safe placeholders for creation. Mock implements the same shapes without HTTP/auth/certificate dependencies, deterministic status and receipt behavior across restart.

Add `/suz-utilisation` to SUZ operations and favorite operation IDs. Form reuses product-group controls, FileTextInput, TraceXSqlImport (opt-in PreserveFullCodes), streamed text JS, ShowMore and the common status visuals. One group/package/scenario per upload; common fields plus editable individual table, row errors/removal, prepared counts, sequential send/cancel/continue, report ID/status/receipt detail and manual resume. Profile changes cancel pending UI work and discard stale results. Filter state key is `crpt-workbench-suz-utilisation-favorite-groups-only`. Version becomes 1.26.0.

## Concrete Steps

Run from `D:\Артём\Projects\C#\СZHELPER`:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

Run tests outside sandbox with approval on the first attempt. Restore with static graph only if required. Do not run builds concurrently in the shared workspace.

## Validation and Acceptance

Test all matrix groups/types/scenarios and conditional dates/fields. Test full GS/crypto strings, templates, Unicode/TSV, exact AI appending, duplicate conflicts and all import paths. Test 29999/30000/30001, GTIN splitting, sequential stop, cancellation after GUID, restart Sending recovery, stale leases, profile/mode isolation, encrypted payload, logs/history secret absence, Auto/Manual, strict guard before token lookup and exact signing bytes. Require zero warnings/errors in documented builds and zero failed full-suite tests. Browser Mock checks include more than one report, result/receipt, favorites synchronization, profile changes, keyboard, ShowMore and 380px width. Check `/`, `/console`, `/history`, `/settings`, `/operations` and new page plus unhandled logs. Inspect diff relative to checkpoint for generated/unrelated files.

## Idempotence and Recovery

Additive migration preserves current tables. Never delete runtime data. Prepared drafts may continue explicitly; Sending/Unknown parts cannot be automatically resent. Abort UI work does not erase received IDs. No final feature commit unless requested. Shutdown is scheduled with a five-minute delay only after complete successful verification, no force flag; report Moscow time and `shutdown /a`.

## Artifacts and Notes

Baseline version 1.25.0; current documented suite 582 passed. Initial checkpoint 8b1b0ad. Update counts to actual final results; don't infer browser interaction from HTTP 200.

## Interfaces and Dependencies

Use ISuzClient, IProfileRepository, ICertificateSignatureService, TimeProvider, FavoriteProductGroupService and existing history repository. Add ISuzUtilisationRepository for job/report persistence with atomic submission/tracking claims. Add SuzUtilisationService (prepare/create/continue/list/check/resume) and SuzUtilisationTracker; rules/preparer are deterministic and share the same matrix with the form. No new framework or external package is required.
