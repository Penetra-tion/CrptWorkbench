# Add CryptoPro-compatible detached CMS signing for GOST certificates

This ExecPlan is a living document. The sections `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds. Maintain this document in accordance with `.agent/PLANS.md`.

## Purpose / Big Picture

CRPT Workbench currently finds the production certificate and its private key but asks the standard .NET `SignedCms` implementation to sign. Modern .NET does not recognize CryptoPro GOST private-key types, so production authentication stops with “Could not determine signature algorithm for the signer certificate.” After this change, GOST 2012 certificates in `LocalMachine/My` are signed through the native Windows CryptoAPI. CryptoAPI follows the certificate's provider metadata and therefore delegates the private-key operation to the installed CryptoPro CSP without exporting the key. Existing RSA behavior remains available for local automated tests. The production True API endpoint allowlist and all mock defaults remain unchanged.

## Progress

- [x] (2026-09-22 14:05Z) Confirmed that failure occurs at `SignedCms.ComputeSignature` after certificate validation and that the selected certificate uses the production `LocalMachine/My` path.
- [x] (2026-09-22 14:08Z) Confirmed from current CryptoPro documentation that modern CryptoPro .NET requires provider-specific CMS and certificate types; the development machine has no CryptoPro .NET NuGet feed or assemblies.
- [x] (2026-09-22 14:04Z) Implemented a focused native CryptoAPI detached-CMS signer and routed supported GOST certificate OIDs through it.
- [x] (2026-09-22 14:05Z) Added automated coverage for algorithm mapping, exact UTF-8 bytes, detached CMS verification, and changed-content rejection.
- [x] (2026-09-22 14:07Z) Updated architecture, deployment documentation, and the dated test baseline to describe the actual in-memory signing path.
- [x] (2026-09-22 14:08Z) Built the Release test project with zero warnings/errors and ran the complete suite outside the restricted sandbox: 73 passed, 0 failed, 0 skipped.
- [x] (2026-09-22 14:09Z) Started the Release app in Mock mode; `/`, `/console`, `/history`, and `/settings` returned HTTP 200 and the server log contained no unhandled exception.
- [x] (2026-09-22 14:10Z) Ran the canonical packaging workflow without skipping tests; deployment ACL checks passed, 73/73 tests passed, and the resulting 422-entry ZIP contained no forbidden runtime or secret files.

## Milestones

The first milestone adds an internal Windows-only CryptoAPI adapter in Infrastructure. `CryptoProCertificateSignatureService` will continue validating certificates and encoding the challenge exactly once as UTF-8 without an automatically added BOM. For GOST 2001, GOST 2012/256, and GOST 2012/512 public-key OIDs it will call `CryptSignMessage` from `crypt32.dll`, supply the matching digest OID, request a detached PKCS#7/CMS result, and include only the signer certificate. The adapter will use the existing `X509Certificate2.Handle`; CryptoAPI reads the certificate’s private-key provider metadata and calls CryptoPro CSP. A native Windows error will become a `CryptographicException`, preserving the existing safe logging and application-level error contract. Non-GOST certificates will retain the standard .NET `SignedCms` path.

The second milestone proves behavior without requiring a production key. A generated RSA certificate will exercise the native adapter directly on Windows, and the resulting CMS will be decoded and verified against the original exact bytes. Pure tests will prove OID routing and ensure unknown algorithms do not silently enter the GOST path. Existing production tests will continue proving the standard fallback and True API safety boundaries.

The final milestone updates `ARCHITECTURE.md` and `docs/deployment/windows-production.md`, builds the web project with the repository’s required flags, and runs the complete Release test project outside the restricted sandbox. Acceptance requires zero build errors and all tests passing; the dated baseline in `AGENTS.md` will be updated only if the test count changes.

## Surprises & Discoveries

- Observation: The official current CryptoPro .NET API no longer patches `System.Security.Cryptography.*`; it requires `CpX509Store`, `CpX509Certificate2`, `CpSignedCms`, and `CpCmsSigner`.
  Evidence: The CryptoPro API-change documentation explicitly says standard runtime-class fixes are no longer implemented and that CMS must use the `Cp` types.
- Observation: This checkout and machine have neither CryptoPro .NET references nor a configured CryptoPro package source, while CryptoPro .NET requires a separate server license.
  Evidence: the Infrastructure project references only the standard PKCS package, `dotnet nuget list source` shows nuget.org and the Visual Studio offline feed, and no `CryptoPro.Security.Cryptography*.dll` was found in the usual installed-product directories.
- Observation: Windows `CryptSignMessage` accepts a certificate context containing `CERT_KEY_PROV_INFO` and uses its registered provider for the private-key operation, which is exactly the metadata attached to a CryptoPro-backed certificate.
  Evidence: Microsoft’s `CRYPT_SIGN_MESSAGE_PARA` documentation requires `CERT_KEY_PROV_INFO_PROP_ID` or `CERT_KEY_CONTEXT_PROP_ID` and accepts explicit hash and signature algorithm identifiers.

## Decision Log

- Decision: Use the native Windows `CryptSignMessage` API for GOST CMS instead of adding CryptoPro .NET packages.
  Rationale: It calls the already installed and licensed CryptoPro CSP through certificate provider metadata, adds no redistributable proprietary assembly or second server-product license, works with the existing `LocalMachine/My` certificate object, and preserves self-contained application packaging. This is narrower than replacing all certificate handling with a new vendor object model.
  Date/Author: 2026-09-22 / Codex
- Decision: Keep standard .NET `SignedCms` for non-GOST algorithms.
  Rationale: Existing generated RSA tests are portable and already prove exact-byte behavior; changing that path would add unnecessary Windows coupling and regression risk.
  Date/Author: 2026-09-22 / Codex
- Decision: Support the explicit digest mappings for GOST 2001 and both GOST 2012 key sizes, but route only recognized GOST public-key OIDs to the native adapter.
  Rationale: Explicit mapping prevents the SHA-256 default from being applied to a GOST key and makes unsupported algorithms fail through their existing path rather than being guessed.
  Date/Author: 2026-09-22 / Codex

## Outcomes & Retrospective

The production signing implementation now recognizes GOST 2001 and both GOST 2012 public-key OIDs and uses native Windows CryptoAPI for their detached CMS signatures. This removes the standard .NET algorithm-discovery failure while retaining the existing certificate store, non-exportable private key, exact challenge bytes, safe logging, and True API allowlist. The native adapter was exercised with an ephemeral RSA certificate because this development machine has no CryptoPro GOST key; the generated detached CMS verified against exact Unicode content and rejected changed content. The full suite passed 73/73, and all four relevant pages returned HTTP 200 in a Release runtime smoke test. The canonical workflow produced `artifacts/windows/CrptWorkbench-win-x64.zip` with SHA-256 `8D06D47648205566DD1A8FE0DBE4AC736701FBF051E86C2D00D9E1DD380D84A5`; it has 422 entries and no forbidden runtime or secret files. A real CryptoPro-backed signing attempt under LocalSystem remains target-server evidence, not something the local test certificate can prove.

## Context and Orientation

`src/CrptWorkbench.Infrastructure/CryptoProCertificateSignatureService.cs` implements the Domain signing port. It opens the Windows machine certificate store, finds the configured thumbprint, validates dates, signature KeyUsage, and private-key association, converts the True API challenge to exact UTF-8 bytes, and currently uses standard .NET `SignedCms`. `src/CrptWorkbench.Clients/ProductionTrueApi.cs` consumes only the Domain interface and must not change. `tests/CrptWorkbench.Tests/ProductionTrueApiTests.cs` verifies detached signatures and exact challenge bytes. `tests/CrptWorkbench.Tests/LoggingTests.cs` verifies that provider messages remain opt-in. `ARCHITECTURE.md` and `docs/deployment/windows-production.md` currently describe the obsolete standard `SignedCms` implementation.

CMS, also called PKCS#7 SignedData, is the envelope sent to True API. “Detached” means the CMS contains the signature and certificate but not a duplicate of the challenge. CryptoAPI is the native Windows cryptographic API in `crypt32.dll`. A certificate context is a Windows pointer owned by `X509Certificate2`; it carries the public certificate and a property telling Windows which CSP container holds the non-exportable private key.

## Plan of Work

Add `src/CrptWorkbench.Infrastructure/WindowsCryptoApiDetachedCmsSigner.cs`. Define only the native structures and `CryptSignMessage` call required for detached signing. Allocate and release unmanaged OID strings and the one-element signer-certificate array in `finally` blocks, pin the input only for the duration of each call, use the two-call size/output pattern, and clear the output buffer after it has been converted to Base64 by the caller. Convert `GetLastWin32Error` to `CryptographicException` so the existing sanitized logging path remains authoritative.

Modify `CryptoProCertificateSignatureService` to recognize GOST public-key OIDs and map each to its correct digest OID. Route recognized GOST algorithms to the native signer on Windows and retain the standard signer for other certificates. If a GOST certificate is encountered on a non-Windows platform, throw a clear platform exception before attempting any native call. Keep cancellation checks before the non-cancellable provider call and before encoding. Do not log challenge bytes, certificate identity, container name, PIN, token, or signature.

Extend `ProductionTrueApiTests.cs` with pure mapping/dispatch assertions and a Windows-only native signing test using the existing generated RSA certificate. Decode the native result with standard `SignedCms`, require `Detached == true`, verify the signature against the original exact UTF-8 bytes, and ensure the content is not embedded. Preserve current RSA tests and all network allowlist tests. Update documentation and the test baseline if the count increases.

## Concrete Steps

Work from `D:\Артём\Projects\C#\СZHELPER`.

Apply the implementation and documentation patches, then restore if project references changed. Build with:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false

Build and run tests using the repository’s Release commands, with the test process outside the restricted sandbox:

    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed

Inspect the final change only under `СZHELPER`; the parent Git repository contains unrelated sibling projects.

## Validation and Acceptance

The new native unit test must create a detached CMS signature whose cryptographic verification succeeds for the original byte sequence and fails if the content changes. Existing exact-byte tests must continue passing for ASCII, Unicode, whitespace, line endings, and an explicit BOM character. Logging tests must continue proving that provider exception text is absent by default and present only when explicitly enabled.

The web project must build with zero errors. The complete Release suite must report zero failed tests. Because this development machine has no CryptoPro-backed GOST test certificate, final acceptance on the target Windows Server still requires one controlled True API authentication attempt or an equivalent local signing check under the service identity; the server log must show the GOST OID, the native signing backend, and successful signature completion without emitting secret material.

## Idempotence and Recovery

All edits are additive or localized and can be rebuilt repeatedly. Native allocations are scoped to one call and freed even on provider failure. No certificate, key container, profile, database, service, or production endpoint is modified by automated tests. If native interop fails in development, the RSA fallback remains independently testable; do not weaken the production allowlist or export a key to work around the issue.

## Artifacts and Notes

The original production failure is:

    Signing failed at ComputeSignature: CryptographicException: Could not determine signature algorithm for the signer certificate.

The expected successful informational sequence after deployment is conceptually:

    Signing certificate validated: ... public key algorithm 1.2.643.7.1.1.1.1, digest algorithm 1.2.643.7.1.1.2.2 ...
    Signing backend selected: Windows CryptoAPI
    Signing completed successfully: signature length ...

## Interfaces and Dependencies

No Domain or Application interface changes. Infrastructure gains an internal static method with the effective contract:

    byte[] WindowsCryptoApiDetachedCmsSigner.Sign(
        ReadOnlySpan<byte> content,
        X509Certificate2 certificate,
        string digestAlgorithmOid)

It is Windows-only, returns encoded detached CMS bytes, includes the end certificate, and throws `CryptographicException` with the native HRESULT on failure. No new NuGet package is introduced.

Revision note (2026-09-22): Initial plan created after confirming the standard .NET GOST-algorithm detection failure and selecting native CryptoAPI as the dependency-free CryptoPro CSP integration path.

Revision note (2026-09-22): Recorded the completed implementation, documentation updates, 73/73 test result, and successful four-page runtime smoke test; canonical package generation remains the final local step.

Revision note (2026-09-22): Marked the plan complete after canonical packaging, deployment ACL regression checks, archive content inspection, and SHA-256 capture.

Revision note (2026-09-22): Superseded for CMS attachment semantics by `.agent/attached-authentication-cms.md`. The native CryptoAPI provider integration remains valid, but True API authentication uses attached CMS (`detached = false`) rather than the detached format originally implemented here.
