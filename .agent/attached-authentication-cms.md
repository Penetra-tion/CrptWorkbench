# Use attached CMS for True API authentication

This ExecPlan is a living document maintained according to `.agent/PLANS.md`. Keep `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` current throughout implementation.

## Purpose / Big Picture

True API authentication requires an attached CMS signature: the original challenge bytes must be embedded inside the PKCS#7/CMS envelope sent as Base64 to `/auth/simpleSignIn`. The application currently creates a detached CMS, which is the format intended for separately transported document content. After this change, both CryptoPro/Windows CryptoAPI production signing and the portable RSA test path create attached CMS, and automated tests prove that the embedded bytes exactly match the challenge returned by `/auth/key`.

## Progress

- [x] (2026-09-22 14:45Z) Compared the supplied example, True API reference section 1.5.2, and every signing call in the repository; confirmed both production and fallback paths use detached mode.
- [x] (2026-09-22 14:50Z) Renamed the signing contract and implementation from detached to attached and switched both CMS backends.
- [x] (2026-09-22 14:52Z) Replaced detached-signature assertions with embedded exact-content and signature-verification assertions.
- [x] (2026-09-22 14:53Z) Updated architecture, deployment guidance, repository baseline descriptions, and added a superseding note to the earlier plan.
- [x] (2026-09-22 14:55Z) Release test build completed with zero warnings/errors and the complete suite passed 75/75 outside the sandbox.
- [x] (2026-09-22 15:00Z) Documented Debug build completed with zero warnings/errors; Mock `/`, `/console`, `/history`, and `/settings` returned HTTP 200.
- [x] (2026-09-22 15:03Z) Canonical packaging passed build, deployment ACL checks, and 75/75 tests; inspected the 422-entry ZIP with no forbidden runtime/secret files.

## Surprises & Discoveries

- Observation: The mismatch is repository-wide rather than limited to the new native adapter.
  Evidence: the Domain method, Infrastructure method, standard `SignedCms`, native `CryptSignMessage`, tests, architecture, deployment guide, and AGENTS guidance all explicitly say detached.
- Observation: True API documents invalid or mismatched signature data as HTTP 400, while the observed server response was HTTP 403.
  Evidence: reference section 1.5.2 lists `Ошибка при проверке подписи` under 400 and `Отсутствует доступ к ресурсу` under 403. The attached-format correction is still required, but organization access may remain a separate server-side issue.

## Decision Log

- Decision: Make the Domain port explicitly attached rather than adding a boolean format parameter.
  Rationale: The only authorized production signing use is `/auth/simpleSignIn`, whose format is fixed. A boolean would allow accidental reintroduction of the wrong mode and suggest unsupported document signing.
  Date/Author: 2026-09-22 / Codex
- Decision: Keep the exact UTF-8 encoding and native CryptoAPI provider path unchanged; change only CMS content attachment semantics.
  Rationale: CryptoPro signing now works under LocalSystem, and the reference requires the same challenge bytes embedded in the signature, not a different encoding or provider.
  Date/Author: 2026-09-22 / Codex

## Outcomes & Retrospective

Implementation is complete. The Domain port, True API token provider, CryptoPro signer, native Windows CryptoAPI adapter, portable .NET fallback, tests, architecture, deployment guide, and repository guidance now consistently use attached authentication CMS. Both signing backends embed the exact UTF-8 challenge bytes. Three portable cases cover ASCII, Unicode/whitespace/line endings, and an explicit BOM character; the native Windows test also decodes the embedded bytes and verifies the signature. Debug and Release builds completed with zero warnings/errors, the complete suite passed 75/75 twice including canonical packaging, and all four Mock pages returned HTTP 200. The canonical artifact is `artifacts/windows/CrptWorkbench-win-x64.zip`, SHA-256 `FF9D074A8F52A230745BE75F633152A2E11957C4D359FD7F547480B73A73FCF9`, with 422 entries and no forbidden runtime or secret files. Target-server `/auth/simpleSignIn` remains the final live proof.

## Context and Orientation

`src/CrptWorkbench.Domain/Contracts.cs` defines the signing port used by `src/CrptWorkbench.Clients/ProductionTrueApi.cs`. `src/CrptWorkbench.Infrastructure/CryptoProCertificateSignatureService.cs` validates the `LocalMachine/My` certificate and dispatches GOST keys to `src/CrptWorkbench.Infrastructure/WindowsCryptoApiCmsSigner.cs`; other keys use standard .NET `SignedCms`. The native API’s `fDetachedSignature` and .NET’s `detached` constructor argument have the same meaning: `false` embeds the content. `tests/CrptWorkbench.Tests/ProductionTrueApiTests.cs` verifies the exact embedded content.

## Plan of Work

Rename `ICertificateSignatureService.SignDetachedPkcs7Base64Async` to `SignAttachedPkcs7Base64Async`, update the token provider and test fakes, and rename the public test helper accordingly. Rename the native signer type/file to remove “Detached” and change both `CryptSignMessage` calls to `detachedSignature: false`. Change standard `SignedCms` construction to `detached: false` and revise operator-facing failure text.

Update the signature tests so decoding without separately supplied content yields the original UTF-8 bytes, including Unicode, whitespace, line endings, and an explicit BOM character. Verify the CMS signature cryptographically. For the native Windows test, require embedded bytes to match exactly and verify the signature. Keep GOST OID routing, logging, allowlist, and full-log tests unchanged.

Update current documentation and repository guidance to say attached authentication CMS. Preserve historical evidence but add a revision note to the earlier completed plan explaining that its detached design was superseded after checking the True API contract.

## Concrete Steps

Work from `D:\Артём\Projects\C#\СZHELPER`.

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed
    PowerShell -NoProfile -ExecutionPolicy Bypass -File .\deploy\windows\Build-Package.ps1

The test and package commands run outside the restricted sandbox because the runner uses a Windows named pipe and deployment checks exercise real temporary ACLs.

## Validation and Acceptance

Each challenge theory case must produce CMS that can be decoded without external content, whose `ContentInfo.Content` equals the exact UTF-8 input bytes, and whose signature verifies. The native Windows test must provide the same evidence through `CryptSignMessage`. All 75 tests must pass, builds must have zero errors, and the Mock pages must remain HTTP 200. The resulting ZIP must contain no database, keys, certificate files, PINs, or tokens.

Target-server acceptance requires installing the package and performing one connection check. The signing log must show Windows CryptoAPI completion and `/auth/simpleSignIn` must return either 200 or a business-access error; a signature-validation 400 is not acceptable.

## Idempotence and Recovery

No persistent data or profile migration is involved. Reinstalling the package retains ProgramData. If target validation regresses, the installer’s existing backup supports rollback. Do not broaden the production allowlist or add document signing as part of this correction.

## Artifacts and Notes

The authoritative behavior is:

    authentication token: attached CMS (`detached = false`)
    separately transported document: detached CMS (`detached = true`), not implemented here

## Interfaces and Dependencies

The final Domain signature is:

    Task<string> SignAttachedPkcs7Base64Async(
        string data,
        string thumbprint,
        CancellationToken cancellationToken = default);

No new package or service registration is required.

Revision note (2026-09-22): Initial plan created after the supplied Java example exposed the detached/attached authentication mismatch.

Revision note (2026-09-22): Closed after attached CMS implementation, exact embedded-content tests, 75/75 test runs, four-page runtime smoke, canonical packaging, archive inspection, and SHA-256 capture.
