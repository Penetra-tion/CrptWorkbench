# Sign challenge bytes through a protected temporary file

This living plan follows [`.agent/PLANS.md`](../PLANS.md).

## Purpose / Big Picture

Implement the user's explicit request to set silent:false and save the True API challenge to a temporary file before signing File.ReadAllBytes bytes. Keep LocalMachine/My and all production allowlists unchanged. The change permits provider PIN UI, but Windows services may not show it and may wait for user interaction.

## Progress

- [x] (2026-09-16) Read signer, tests, deployment guide, and plan requirements.
- [x] (2026-09-16) Implemented protected temporary directory, strict UTF-8 without added BOM, file-byte signing, finally cleanup, and silent:false diagnostics.
- [x] (2026-09-16) Debug web build completed with zero warnings/errors; updated three exact-byte signature cases and dated baseline to 67 tests.
- [x] (2026-09-16) Full package tests: 67 passed, 0 failed/skipped; real ACL checks passed; Release builds zero warnings/errors. No temporary signing directories remained after tests.
- [x] (2026-09-16) Mock runtime pages and liveness all HTTP 200, no unhandled exceptions, app stopped. ZIP has 422 entries, matching signer DLL, ACL helper and no forbidden runtime/secret files; scoped source list contains no generated outputs.

## Surprises & Discoveries

The application receives a string challenge rather than a path. The user chose temporary-file serialization explicitly after clarification. UTF-8 serialization must not add a BOM or normalize line endings. A BOM character already present in the challenge must remain part of the signed bytes.

## Decision Log

Use a unique random directory in the service's temporary directory. Create it with a protected Windows ACL granting only the current process account full control before writing contents; Unix fallback uses owner-only access. Save strict UTF-8 without a BOM and read bytes through File.ReadAllBytes. Zero the byte buffer and delete the exact file and empty directory in finally without recursive deletion. Report cleanup failure safely and fail the operation rather than silently retaining a file. Do not log file paths/content. Date/Author: 2026-09-16 / Codex.

## Context and Orientation

`src/CrptWorkbench.Infrastructure/CryptoProCertificateSignatureService.cs` implements the Domain signing port and exposes the static testable detached PKCS#7 helper. `tests/CrptWorkbench.Tests/ProductionTrueApiTests.cs` verifies cryptographic signatures against original exact UTF-8 bytes for ASCII, Unicode/whitespace/line endings and explicit BOM-character inputs. `LoggingTests.cs` now requires silent false in the real log sink. `docs/deployment/windows-production.md` documents PIN/session limitations and temporary-data lifetime. No endpoint contracts change.

## Plan of Work

Replace direct in-memory UTF-8 conversion with protected directory creation, file writing and File.ReadAllBytes. Use one SilentSigning=false constant for actual signing and all diagnostic messages. Track ChallengeFileWrite/Read/Cleanup stages. Preserve existing certificate checks, detached SignedCms, EndCertOnly and Base64 response format. Expand existing signature test into three cases and update the dated baseline.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, run:

    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    PowerShell -NoProfile -ExecutionPolicy Bypass -File .\deploy\windows\Build-Package.ps1

Run packaging outside the restricted sandbox with approval. Start the Debug app in Mock mode in an isolated ignored artifacts data directory and check `/`, `/console`, `/history`, `/settings` return 200; stop it. Inspect temporary signing directories and ZIP contents after tests, without deleting any preexisting unrelated data.

## Validation and Acceptance

Expect 67 passed tests, zero failures and zero build warnings/errors, including actual temporary-file signing for three input forms and log assertions for silent false. Verify no newly left CrptWorkbench-sign-* directories after normal test execution, and no data/key/certificate files in the ZIP. Deploy the full ZIP using the corrected installer, reproduce the target signing failure once, and inspect the safe HRESULT. RSA tests do not prove a CryptoPro/GOST certificate signs successfully.

## Idempotence and Recovery

Each attempt creates a new unpredictable directory; normal success/error cleanup deletes only that attempt's file and empty directory. Unexpected process termination can leave protected challenge data on disk; deletion is not a forensic secure erase. Cleanup failures are visible and prevent a successful result. No PFX, keys, PIN, token or signature is written to the temporary file. No store migration, schema change or new production endpoint.

## Outcomes & Retrospective

Implementation, Debug/Release builds, full tests, mock runtime checks and package inspection are complete. The helper signs File.ReadAllBytes from a protected temporary UTF-8 file, uses silent:false, and removes temporary artifacts on normal completion/error. Tests verify exact detached signature bytes for ASCII, Unicode/line endings/whitespace and an explicit BOM character. Interactive PIN and actual CryptoPro/GOST behavior must still be checked on the server; no production request was made locally. Existing static-file framework warning was observed during Debug smoke and is unrelated to this signing change.

## Artifacts and Notes

Deliverable remains `artifacts/windows/CrptWorkbench-win-x64.zip`, built by the existing packaging script with tests, never SkipTests.

SHA-256: `5B9D64245D0BD7D641D9479B740658ECFD3589F2897616D0F10E4C216C3106F0`. Verification: 67 passed / 0 failed / 0 skipped, no build warnings/errors; `/`, `/console`, `/history`, `/settings`, `/health/live` HTTP 200 in Mock mode on port 5097 using isolated ignored `artifacts/signing-file-smoke` data. Temporary signing directories absent after the tests.

## Interfaces and Dependencies

The Domain port still accepts string data and certificate thumbprint; only Infrastructure serializes it. Uses .NET FileSystemAclExtensions, DirectorySecurity, File.ReadAllBytes and SignedCms without new packages. No UI transport logic changes.

Revision note (2026-09-16): Created plan during the user-authorized signing change and recorded exact encoding, temporary-file protection and PIN/session limitations.

Revision note (2026-09-16): Completed local verification and packaging, recorded hash and exact-byte/cleanup evidence; target-server signing verification remains distinct from local RSA/mock checks.


Revision note (2026-09-18): Archived completed plan; historical implementation and verification evidence retained. Current repository guidance is ../../AGENTS.md.
