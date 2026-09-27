# Repair Windows service updates and backup access

This living ExecPlan follows [`.agent/PLANS.md`](../PLANS.md).

## Purpose / Big Picture

An administrator can update an existing Windows service without native-command quoting failures and restore backups whose inherited access was removed by an older installer. The packaged scripts preserve existing runtime data and backups. No application endpoint or production integration changes.

## Progress

- [x] (2026-09-16) Read installer, restore, package script, deployment guide, and plan instructions.
- [x] (2026-09-16) Replace service-path configuration, separate root protection from recursive grants, repair backup access before rollback, and add non-mutating regression checks.
- [x] (2026-09-16) Regression checks passed; documented Debug web build completed with zero errors/warnings.
- [x] (2026-09-16) Server packaging completed: Release builds zero warnings/errors, installer checks passed, application tests 63 passed / 0 failed, ZIP contains corrected scripts and no forbidden runtime files.
- [x] (2026-09-16) Recorded final evidence and target-server verification limits. Generated release outputs remain ignored; no runtime data or secrets added to the scoped untracked-file list.
- [x] (2026-09-16) Reopened after target-server screenshots showed protected empty DACLs remaining despite successful native grants. Manual direct ACL repair restored the service and `/health/live` returned 200.
- [x] (2026-09-16) Replaced all installer/restore native ACL commands with shared DACL-only Windows API access, explicit existing-file grants, inheritable directory grants, persisted-rule verification, and reparse-point rejection.
- [x] (2026-09-16) Real Windows PowerShell ACL tests reproduced unreadable empty-DACL files and verified recovery, repeatability, nested files, and future-file inheritance. Debug web build zero errors/warnings.
- [x] (2026-09-16) Replacement ZIP packaging completed: all real ACL checks passed, 63 application tests passed / 0 failed, Release builds zero errors/warnings. All three ZIP scripts exactly match source; 422 entries and no forbidden data/secrets. Removed the temporary fixture left by the initial privilege failure.

## Surprises & Discoveries

The installer used one native argument containing both `binPath=` and its quoted value, while hiding command output. It also removed inheritance recursively from the entire data tree containing backups. The screenshot shows a service-configuration failure followed by denied access to a backup database. Child permissions must not be stripped when protecting the root.

Target-server evidence showed native grants returning success while backup files and the live database still had empty protected DACLs. Initial mocked tests asserted argument lists, not actual permissions, so they did not establish recovery. New real-file tests caught `SeSecurityPrivilege` being requested by broad Set-Acl persistence on the development account. Reading and writing only Access sections through FileInfo/DirectoryInfo fixed that requirement and preserved ownership/audit settings.

## Decision Log

Use built-in `Invoke-CimMethod` with `Win32_Service.Change`, setting only PathName and StartMode and reporting its return code. This avoids Windows PowerShell native quoting differences and leaves service credentials unchanged. Protect data inheritance only at the root and separately grant SYSTEM/Administrators full control recursively. Preserve legacy App_Data in backups rather than silently deleting a possible database. Manual restore verifies every backup file is readable before replacing binaries. Date/Author: 2026-09-16 / Codex.

Replace icacls entirely rather than trusting exit codes. Shared `deploy/windows/AccessControl.ps1` uses GetAccessControl(Access)/SetAccessControl so only the file access list changes. Grants are established before removing root inheritance in the same descriptor, then saved rules are reread. Repair directories before enumerating children. Preserve unrelated explicit grants and fail on conflicting direct SID denies; do not silently remove them. Reject reparse points to keep recursive work scoped. Real tests replace ACL mocks; service calls stay mocked. Date/Author: 2026-09-16 / Codex.

## Outcomes & Retrospective

The original package passed mocked tests but failed actual target-server ACL recovery. Manual direct grants restored the service and liveness. The replacement shared ACL implementation now passes real temporary-file recovery tests and full Release packaging is complete. Installation of the replacement package still requires target-server verification. No app runtime or endpoint behavior changed and no live production API request was executed. The synthetic temporary fixture left by the initial failed privilege test was removed after permission repair; it contained no user data.

## Context and Orientation

`deploy/windows/Install-Service.ps1` replaces binaries below Program Files and protects data below ProgramData. An ACL is the Windows list of file access permissions; its DACL is the part granting or denying file access. `deploy/windows/Restore-PreviousVersion.ps1` restores the newest timestamped backup and repairs runtime-data access before start. Both load `deploy/windows/AccessControl.ps1`. `deploy/windows/Build-Package.ps1` publishes the self-contained application and copies all three scripts and `docs/deployment/windows-production.md` into the ZIP. `tests/deployment/Installer.Tests.ps1` loads installer helper definitions through the PowerShell syntax tree, mocks service calls, and exercises actual ACL persistence in newly created temporary files. These fixtures contain synthetic text, never real secrets.

## Plan of Work

Implement Set-ExistingServiceConfiguration and Set-ProtectedDirectoryAccess in the installer. Protect data before creating backups and after replacement, restore explicit backup grants on rollback, and wait for the service to stop before restoration. In manual restore, validate the backup path, require administrator privileges, repair backup grants, and preflight all file reads. Run regression checks in normal release packaging. Document recovery using the new extracted scripts, not the old ZIP.

Replace all ACL helper bodies with calls to Set-WorkbenchTreeAccess in AccessControl.ps1. For data use ProtectedData and optional account Modify; for application enable inheritance and grant SYSTEM ReadAndExecute, Administrators FullControl, optional account ReadAndExecute. Every existing object gets explicit grants; directory grants also inherit to newly created files. SetAccessRuleProtection applies only to data root, or enables inheritance on each application object. Include the new helper file in packaging. Add real-file fixture tests for unreadable protected empty DACLs, nested legacy files, repeated application, future-file inheritance, ownership preservation, and explicit-deny verification failure.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, run:

    powershell -NoProfile -ExecutionPolicy Bypass -File tests/deployment/Installer.Tests.ps1
    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    PowerShell -ExecutionPolicy Bypass -File .\deploy\windows\Build-Package.ps1

The ACL regression command and packaging command must run outside the restricted sandbox with approval. Packaging also runs the Microsoft Testing Platform suite.

## Validation and Acceptance

Expect real Windows ACL checks and installer regression checks to pass, zero build warnings/errors, and 63 passed application tests with zero failures. Inspect ZIP scripts for the CIM update and dot-sourced AccessControl.ps1, verify the helper matches source, and inspect entries for forbidden databases, keys, and certificates. On the already manually recovered server extract the whole replacement ZIP and run elevated Install-Service.ps1, then check Running and `/health/live` HTTP 200. A broken server can instead run the new manual rollback first. Do not perform authentication or production API requests as part of this fix.

## Idempotence and Recovery

Installer backups retain old files, including legacy data. Protected root ACLs and recursive explicit grants can be applied repeatedly without removing backup contents. Paths must be validated before recursive replacement. Manual restore checks backup readability before removing application files; runtime data under ProgramData is never deleted. Retain old backups for recovery.

## Artifacts and Notes

Initial verification: installer regression checks passed; Debug web build: zero warnings and errors. Deliverable: `artifacts/windows/CrptWorkbench-win-x64.zip` after successful packaging.

Final packaging: Release web and test builds zero warnings/errors, 63 application tests passed, 0 failed, 0 skipped; installer checks passed. ZIP SHA-256: `066CDD3099BDF493439F4AB38BDB34FFF08E90F8B09AE4A0972D89473111CF5B`.

Replacement deliverable: `artifacts/windows/CrptWorkbench-win-x64.zip`, SHA-256 `75FCA85EF3220B671AB310FF0E8EC87C504CF1134FE6AF88E46C050D89355A1F`. ZIP verification: 422 entries, Install-Service.ps1 / Restore-PreviousVersion.ps1 / AccessControl.ps1 exactly match source, no native ACL mutations, and no database/key/certificate/log entries. Real ACL integration checks and all 63 application tests passed; builds reported zero warnings/errors.

## Interfaces and Dependencies

No application dependencies change. Deployment targets Windows PowerShell 5.1 with built-in CIM/service cmdlets and .NET Framework FileInfo/DirectoryInfo access-control APIs. No native ACL command remains. Set-WorkbenchTreeAccess(Path, ProtectedData, AdditionalAccount, AdditionalRights) is shared by install and restore. Regression checks require no Pester dependency.

Revision note (2026-09-16): Created and populated the living plan during the focused deployment repair; Release packaging and server verification remain distinguished from isolated checks.

Revision note (2026-09-16): Closed local implementation and packaging verification with test and hash evidence; elevated target-server acceptance remains an operator step.

Revision note (2026-09-16): Reopened after actual server failure; replaced native ACL grants with a shared DACL-only API, added real Windows ACL tests, and documented manually confirmed server recovery. Earlier ZIP hash is historical, not the replacement deliverable.

Revision note (2026-09-16): Completed replacement packaging and verified bundled scripts byte-for-text against source; recorded replacement SHA-256, real ACL/full-suite evidence, and cleanup. Target-server update acceptance remains separate.


Revision note (2026-09-18): Archived completed plan; historical implementation and verification evidence retained. Current repository guidance is ../../AGENTS.md.
