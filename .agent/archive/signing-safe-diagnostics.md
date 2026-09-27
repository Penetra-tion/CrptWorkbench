# Add safe signing diagnostics

This living plan follows [`.agent/PLANS.md`](../PLANS.md).

## Purpose / Big Picture

Operators can reproduce a failed certificate check once and read the signing stage and numeric cryptographic error in the server log, without exposing secrets. Keep LocalMachine/My, silent detached SignedCms signing, and all network allowlists unchanged.

## Progress

- [x] (2026-09-16) Read signing implementation, safe log sink, tests, deployment guide, and plan instructions.
- [x] (2026-09-16) Added ILogger diagnostics, stage/error codes, scoped safe properties, and two file-log test cases.
- [x] (2026-09-16) Current-source Debug/Release builds zero warnings/errors; server package completed with 65 passed / 0 failed / 0 skipped and real ACL checks passed.
- [x] (2026-09-16) Mock runtime `/`, `/console`, `/history`, `/settings`, `/health/live` all HTTP 200; no unhandled exceptions, app stopped. Existing static-file framework warning remains visible and is unrelated to signing.
- [x] (2026-09-16) ZIP DLLs match published outputs, AccessControl.ps1 retained, 422 entries and no forbidden runtime/secret files. Scoped untracked list contains only source/doc files; artifacts data remains ignored.

## Surprises & Discoveries

The signature service previously wrapped CryptographicException without logging it. The safe sink also strips all unapproved properties and replaces exception-bearing log templates, so passing an exception to ILogger would lose useful context and risk revealing provider text.

## Decision Log

Log fixed message templates with primitive fields, never exception objects or messages. Use the base exception type and hexadecimal HRESULT, a numeric Windows error code. Permit new signing fields only for the exact signer category. Include store, silent mode, process bits and LocalSystem boolean; omit identity names, certificate subject/thumbprint and container names. Do not probe CurrentUser or access extra private keys. Date/Author: 2026-09-16 / Codex.

## Context and Orientation

`src/CrptWorkbench.Infrastructure/CryptoProCertificateSignatureService.cs` signs challenges using a LocalMachine/My certificate and detached SignedCms. `src/CrptWorkbench.Web/WorkbenchLogging.cs` filters all events before Serilog writes server/client files. `tests/CrptWorkbench.Tests/LoggingTests.cs` tests the complete file sink with an RSA test certificate and a public-only certificate. `docs/deployment/windows-production.md` tells operators where to find the new events. No API operation contract changes, so CRPT protocol PDFs are not required for this diagnostics-only task.

## Plan of Work

Inject ILogger into the signer. Log the beginning of store lookup and validation, successful signature creation, and failures at fixed named stages. Format numeric HResult without using exception text. Allow metadata only from the exact signer category. Use real file sink tests to assert correlation, failure-stage/error-code visibility and exclusion of challenge/certificate subject/thumbprint. Update the dated application test baseline from 63 to 65. Rebuild the existing server package script with full tests.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, run:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    PowerShell -NoProfile -ExecutionPolicy Bypass -File .\deploy\windows\Build-Package.ps1

Packaging must run outside the sandbox with approval; it includes actual temporary ACL checks and the Microsoft Testing Platform suite. Start the Debug app in Mock mode using an isolated ignored artifacts data directory; check `/`, `/console`, `/history`, `/settings` return 200, and stop it. Do not perform a production certificate check on the development machine.

## Validation and Acceptance

Expect zero build warnings/errors and 65 passed application tests, zero failed. SigningDiagnosticsReachServerLogWithoutSensitiveData verifies both success and certificate-validation failure plus retained synthetic cryptographic code. Inspect the ZIP for the updated signer DLL, preserved AccessControl.ps1, and no databases, keys or certificates. The actual GOST failure code must be obtained after deploying the ZIP and clicking Check on the target server; RSA tests cannot prove CryptoPro/GOST access works.

## Idempotence and Recovery

No persisted schema, signing mode, certificate store, or integration behavior changes. Keep runtime smoke data below ignored artifacts and never delete existing data. Install via the already reviewed new ACL installer, retaining backups. No commits or unrelated edits.

## Outcomes & Retrospective

Implementation and local verification are complete. Default Information logs show signing context; failures at Error carry fixed stage, base exception type and hexadecimal HRESULT without exception text. Two real file-sink test cases verify success/failure events, retained synthetic cryptographic HRESULT, correlation, and absence of challenge, subject and thumbprint. CryptoPro/GOST diagnosis remains a target-server action; local RSA tests do not reproduce its unknown failure. No production request was performed.

## Artifacts and Notes

Expected useful event: Signing failed at ComputeSignature: type CryptographicException, HRESULT 0xXXXXXXXX, store LocalMachine/My, silent true. This example is illustrative, not evidence of a specific target-server error.

Deliverable: `artifacts/windows/CrptWorkbench-win-x64.zip`. SHA-256: `2818560C45EF84D1EEF6F9EBF391943FDF21FD545F4426367E163BE2EB198DA5`. Full application suite: 65 passed, 0 failed, 0 skipped. Real Windows ACL checks passed. Runtime checks used isolated ignored `artifacts/signing-diagnostics-smoke` with Mock mode and port 5097; no original database or profile was changed.

## Interfaces and Dependencies

ILogger<CryptoProCertificateSignatureService> is injected through existing DI registration. The existing public static signing helper accepts an optional typed logger for file-sink tests; ordinary callers remain compatible. No packages or layer references change.

Revision note (2026-09-16): Created and populated the living plan after implementing the focused diagnostics flow; remaining work is packaging/runtime verification and final evidence.

Revision note (2026-09-16): Closed implementation with file-log tests, clean builds, mock runtime checks, release artifact hash and ZIP inspection. Target-server reproduction is intentionally not claimed as verified.


Revision note (2026-09-18): Archived completed plan; historical implementation and verification evidence retained. Current repository guidance is ../../AGENTS.md.
