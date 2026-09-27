# Implement package transformation with durable document tracking

This ExecPlan is a living document maintained under `.agent/PLANS.md`.

## Purpose / Big Picture

An operator loads a two-column TXT file (item and parent package), checks one action at a time, submits one signed True API document per parent package, and sees processing statuses. Tracking survives a server restart and pauses after 24 hours until resumed.

## Progress

- [x] (2026-09-25) Verified True API v515 sections 4.1.1 and 4.1.2.2 and existing read-only document status behavior.
- [x] (2026-09-25) Added validated preview and per-package document construction.
- [x] (2026-09-25) Added detached signing and exact create-document production allowance.
- [x] (2026-09-25) Persisted submissions and implemented restart-safe status polling.
- [x] (2026-09-25) Added drag/drop UI, tests, Release build, 177 passing tests, and HTTP 200 smoke checks.

## Surprises & Discoveries

- The contract contains exactly one `uitu` parent per `REAGGREGATION_DOCUMENT`; a multi-parent TXT must produce separate documents and GUIDs.
- A child uses either `uit_uitu` or `kitu`, exclusively; the two-column TXT does not convey this type. The preview must let the operator identify nested BOX/SET children.
- Existing signature service produces attached CMS for authentication. Document creation explicitly needs detached CMS.

## Decision Log

- Decision: Only one action (ADDING or REMOVING) is submitted per batch; group rows by parent and submit separate documents. Date/Author: 2026-09-25 Codex.
- Decision: Preserve submission intent before HTTP; uncertain outcomes are never automatically retransmitted. Date/Author: 2026-09-25 Codex.
- Decision: Poll for at most 24 hours, then pause until manual renewal. Date/Author: 2026-09-25 Codex.

## Outcomes & Retrospective

The Mock and Production paths are implemented. Release build has zero warnings and the 177-test suite has zero failures. A separate temporary Mock database migrated successfully and all relevant pages returned HTTP 200. The published app was exercised in a browser: one row produced a GUID, background polling reached `CHECKED_OK`, and restart preserved both. Actual CRPT submission and certificate use require validation in the target Production environment.

## Context and Orientation

`src/CrptWorkbench.Application` holds use cases; `src/CrptWorkbench.Clients` hosts mock and real True API adapters; `src/CrptWorkbench.Infrastructure` has EF Core SQLite and CryptoPro signing; `src/CrptWorkbench.Web` hosts Blazor UI and background work. Existing document info is a read-only GET and is used for status polling. Existing production guards must be extended only for `POST /api/v3/true-api/lk/documents/create?pg=<allowed-group>`.

## Plan of Work

Add a parser for UTF-8 tab-separated rows, rejecting invalid and duplicate pairs while retaining line numbers. Add one persisted submission record per parent with document body and submission state. Build JSON `participant_inn`, `reaggregation_type`, `uitu`, `uit_uitu_list`, sign the UTF-8 bytes as detached CMS, and create a MANUAL document with type `REAGGREGATION_DOCUMENT`. Parse returned GUID and commit it immediately. Poll document info with the existing client; persist last status, error and next attempt. On restart, claim expired work. The UI displays preview, submission records, refresh and resume controls.

## Concrete Steps

Run from `D:\Артём\Projects\C#\СZHELPER`: `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`; build and run the Release test project with repository commands. Inspect `/special-features` in Mock mode. Review final diff scoped to `СZHELPER`.

## Validation and Acceptance

A two-column TXT produces grouped preview, clear line errors, and one GUID per parent in mock mode. Polling eventually stores the terminal status. Restarting Web preserves records and resumes pending checks. After 24 hours, automatic checks pause and a manual resume opens another 24-hour period. Tests verify document serialization, signature byte identity, production allowlist, ambiguous submit failure, retry schedule, migration and mock-only network behavior.

## Idempotence and Recovery

Migrations are additive. Creation never automatically retries after an unknown network outcome. Polling is read-only and can retry; expired claims allow recovery after a crash. Preserve existing SQLite and runtime secrets.

## Interfaces and Dependencies

Use the Domain certificate signing abstraction and True API client port. Add a repository port for persisted transformation records in Domain, EF implementation in Infrastructure, and orchestrating Application service. Add one hosted service in Web for polling. No new external libraries are required.

## Artifacts and Notes

Reference: `docs/crpt-reference/true-api-gis-mt-v515-2025-06-04.pdf`, pages 55-57 and 65-69.

2026-09-25 revision: Marked implementation and local verification complete; recorded the unresolved live-service validation boundary.
