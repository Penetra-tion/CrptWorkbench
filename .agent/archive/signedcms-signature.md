# Replace CryptoPro CLI signing with SignedCms

This ExecPlan is a living document. The sections `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds. This document follows [`.agent/PLANS.md`](../PLANS.md).

## Purpose / Big Picture

CRPT Workbench previously shelled out to CryptoPro `csptest.exe` to sign the True API authorization challenge. The user has a known-working .NET `SignedCms` detached PKCS#7 signing flow and wants the application to use that flow while keeping certificates in `LocalMachine/My`. After this change, pressing the production connection check signs the challenge in-process with `SignedCms`, avoiding the old `csptest.exe` exit-code failure path while preserving the existing production network allowlist.

## Progress

- [x] (2026-09-15 23:44Z) Read repository signing code, tests, production readiness, install script, and deployment docs.
- [x] (2026-09-15 23:50Z) Replaced the signing service and interface contract with detached PKCS#7 `SignedCms`.
- [x] (2026-09-15 23:53Z) Updated DI, readiness, installer, and production documentation so `csptest.exe` is no longer required.
- [x] (2026-09-15 23:57Z) Updated production authorization tests to verify detached CMS behavior and remove CLI process tests.
- [x] (2026-09-15 23:59Z) Ran restore, web build, test build, and tests; the full test run required escalation because the Windows sandbox hung the Microsoft Testing Platform IPC.

## Surprises & Discoveries

- Observation: The installer validates and injects `Crpt__CryptoProExecutablePath`, so changing only the signer would still leave deployment blocked by a now-unused executable path.
  Evidence: `deploy/windows/Install-Service.ps1` validates `$CryptoProExecutablePath` and writes `Crpt__CryptoProExecutablePath=$resolvedCryptoProPath`.
- Observation: The sandboxed `dotnet test` process hung without output, but the same filtered and full test commands completed outside the sandbox.
  Evidence: Filtered `CryptoProSignerCreatesDetachedPkcs7Signature` passed in 1.886s, and the full suite passed with 59 tests in 3.921s outside the sandbox.

## Decision Log

- Decision: Keep the existing signer class name for a scoped edit, but rename the interface method to `SignDetachedPkcs7Base64Async`.
  Rationale: The DI surface stays simple while the contract accurately states detached PKCS#7 behavior.
  Date/Author: 2026-09-15 / Codex.
- Decision: Test the CMS construction through a static helper that accepts an `X509Certificate2`, while the production instance method still opens `LocalMachine/My`.
  Rationale: Unit tests can create an ephemeral self-signed certificate without writing to the machine certificate store, and production behavior still honors the requested store.
  Date/Author: 2026-09-15 / Codex.

## Outcomes & Retrospective

The implementation now signs True API authorization challenges with detached PKCS#7 `SignedCms` using certificates from `LocalMachine/My`. The application no longer requires `csptest.exe` for readiness or installation, and the production allowlist remains unchanged. The old process-timeout and temporary-file cleanup tests were removed because no external process or temporary signing files remain; the test baseline is now 59 passed tests.

## Context and Orientation

The True API token provider in `src/CrptWorkbench.Clients/ProductionTrueApi.cs` asks `ICertificateSignatureService` to sign the challenge returned by `/auth/key`, then sends the signature as JSON field `data` to `/auth/simpleSignIn`. The signer in `src/CrptWorkbench.Infrastructure/CryptoProCertificateSignatureService.cs` now uses `SignedCms` in-process. The app validates production readiness in `src/CrptWorkbench.Web/ProductionReadinessService.cs`, installs the Windows service with `deploy/windows/Install-Service.ps1`, and documents production setup in `docs/deployment/windows-production.md`.

Detached PKCS#7 means the encoded CMS signature does not include the original challenge bytes. `SignedCms(contentInfo, detached: true)` creates that detached signature.

## Plan of Work

Replace the signing interface method name and update all callers and fakes. Rewrite `CryptoProCertificateSignatureService` so it opens `X509Store(StoreName.My, StoreLocation.LocalMachine)`, finds the selected thumbprint, validates the certificate, signs UTF-8 challenge bytes with `SignedCms` detached mode, and returns compact Base64. Add `System.Security.Cryptography.Pkcs` to Infrastructure.

Remove the now-unused external process runner registration and readiness check for `csptest.exe`. Make the installer parameter obsolete by no longer validating or exporting it, but keep accepting it so existing deployment commands do not break. Update docs to describe CryptoPro CSP/provider and detached PKCS#7 signing without `csptest.exe`.

Update tests to assert the token provider sends the renamed signature, and add a self-signed certificate test that decodes the Base64 CMS, verifies the signature against the original challenge, and confirms the CMS is detached.

## Concrete Steps

Run commands from `D:\Артём\Projects\C#\СZHELPER`. Use `dotnet restore CrptWorkbench.sln -p:RestoreUseStaticGraphEvaluation=true` if adding the package reference makes no-restore build fail because assets are stale.

## Validation and Acceptance

Run:

    dotnet build src\CrptWorkbench.Web\CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests\CrptWorkbench.Tests\CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests\CrptWorkbench.Tests\CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

Acceptance is zero build errors, zero introduced warnings, and all tests passing. The new CMS test must prove that the signature is detached and valid for the challenge bytes.

## Idempotence and Recovery

All edits are source-only and can be repeated safely. If package restore is required, use the existing solution restore command. Do not delete runtime data or certificate material.

## Artifacts and Notes

Verification output:

    dotnet restore CrptWorkbench.sln -p:RestoreUseStaticGraphEvaluation=true
    Restored all projects successfully.

    dotnet build src\CrptWorkbench.Web\CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    Сборка успешно завершена.
    Предупреждений: 0
    Ошибок: 0

    dotnet build tests\CrptWorkbench.Tests\CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    Сборка успешно завершена.
    Предупреждений: 0
    Ошибок: 0

    dotnet test --project tests\CrptWorkbench.Tests\CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed
    Сводка тестового запуска: Пройден!
    итог: 59
    сбой: 0
    успешно: 59
    пропущено: 0

## Interfaces and Dependencies

`ICertificateSignatureService` must expose:

    Task<string> SignDetachedPkcs7Base64Async(string data, string thumbprint, CancellationToken cancellationToken = default);

Infrastructure must reference `System.Security.Cryptography.Pkcs` version `10.0.8`.


Revision note (2026-09-18): Archived completed plan; historical implementation and verification evidence retained. Current repository guidance is ../../AGENTS.md.
