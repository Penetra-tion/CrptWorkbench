# Make Windows service installation resilient to broken ACLs

This ExecPlan is a living document. The sections `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must be kept up to date as work proceeds. Maintain this document in accordance with [`.agent/PLANS.md`](../PLANS.md).

## Purpose / Big Picture

An administrator must be able to install or update CRPT Workbench repeatedly even when a prior release left restrictive Windows file permissions in `C:\Program Files\CrptWorkbench`. After this change, the installer repairs only that validated application directory, preserves normal `Program Files` inheritance, grants the machine account enough access to run the application, keeps runtime data protected separately, and attempts to restore a previously running version if an update fails.

## Progress

- [x] (2026-09-15 20:30Z) Inspected the current installer, rollback script, packaging script, deployment guide, and repository ExecPlan rules.
- [x] (2026-09-15 20:46Z) Updated `deploy/windows/Install-Service.ps1` with access repair, package unblocking, executable preflight, and update recovery.
- [x] (2026-09-15 20:48Z) Updated deployment documentation to describe the corrected permission model and administrator/unblock behavior.
- [x] (2026-09-15 20:50Z) Parse-checked all four deployment PowerShell scripts; each reported `syntax OK`. The parent Git repository treats the scoped project files as untracked, so verification uses direct file inspection rather than a tracked diff.
- [x] (2026-09-15 21:02Z) Built the self-contained ZIP with zero build warnings/errors and completed the full suite: 61 passed, 0 failed, 0 skipped.
- [x] (2026-09-15 21:04Z) Inspected the ZIP: corrected installer and guide present, application inheritance repair present, data isolation retained, and zero forbidden runtime/secret entries. Recorded SHA-256.

## Milestones

The first milestone changes the installer. It is complete when the script can normalize a legacy protected application tree before backup, install files with inherited `Program Files` permissions, verify the executable is readable, and restore a backup only when an update interrupted a service that had previously been running.

The second milestone updates operator documentation and produces the release artifact. It is complete when all PowerShell files parse, the .NET build has zero errors and warnings, all 61 tests pass, and `artifacts\windows\CrptWorkbench-win-x64.zip` is rebuilt with the corrected installer.

## Surprises & Discoveries

- Observation: The failing server could create and configure the service, but both an elevated administrator and `LocalSystem` were denied access to `CrptWorkbench.Web.exe`.
  Evidence: Resetting ownership and inherited ACLs on `C:\Program Files\CrptWorkbench` allowed the executable and service to start.

- Observation: The current installer removes inherited permissions from both the application and data directories.
  Evidence: `deploy/windows/Install-Service.ps1` passes `/inheritance:r` to `icacls.exe` for both trees; the application tree does not need this isolation, while the data tree does.

- Observation: The parent Git repository currently reports the scoped CRPT Workbench files as untracked.
  Evidence: `git status --short -- СZHELPER/...` returns `??` for the edited installer, guide, and this ExecPlan, while `git ls-files` returns no installer entry. No unrelated parent paths were modified.

## Decision Log

- Decision: Keep standard inherited permissions for the application directory and explicitly grant `SYSTEM` read/execute plus built-in Administrators full control.
  Rationale: Application binaries are not secrets, and retaining the established `Program Files` permission model avoids locking administrators or the service out. The database and Data Protection keys remain in the separately hardened data directory.
  Date/Author: 2026-09-15 / Codex

- Decision: Normalize a legacy installation before reading it for backup, but only after the path has passed the existing `Program Files` child-path validation.
  Rationale: This repairs releases already affected by the bug without widening the target of ownership or ACL changes.
  Date/Author: 2026-09-15 / Codex

- Decision: Restore old binaries automatically only when the service was running before the update and a complete backup exists.
  Rationale: This recovers a known working deployment without attempting to revive a previously broken or incomplete installation.
  Date/Author: 2026-09-15 / Codex

## Outcomes & Retrospective

The installer now repairs ACLs left by earlier releases before backup, retains normal `Program Files` inheritance for new binaries, unblocks transferred files, validates executable readability, and restores a previously running deployment after a failed update. Runtime data remains separately protected. All deployment scripts parse, the Release build has zero warnings and errors, all 61 tests pass, and the final ZIP contains no forbidden data or secret files. Live clean-install and damaged-ACL upgrade execution must still be performed on the target Windows Server because the development host is not that service environment.

## Context and Orientation

`deploy/windows/Install-Service.ps1` installs the packaged self-contained application, creates or updates the `CrptWorkbench` Windows service, and starts it as `LocalSystem`. The application files live below `Program Files`; the SQLite database, Data Protection keys, and binary backups live below `ProgramData`. An ACL is a Windows access-control list. The application directory should inherit normal `Program Files` access, while the data directory intentionally disables inheritance and permits only `SYSTEM`, Administrators, and an explicitly supplied custom service account.

`deploy/windows/Build-Package.ps1` publishes the app and copies the installer plus the guide into `artifacts\windows\CrptWorkbench-win-x64.zip`. `docs/deployment/windows-production.md` is also copied into that ZIP as `DEPLOYMENT.md`.

## Plan of Work

Add focused helper functions to `deploy/windows/Install-Service.ps1` for invoking `icacls`, normalizing an existing installation, applying final application permissions, testing that the installed executable can be opened for reading, and reporting service diagnostics. Unblock all package source files before copying. Record whether the service existed and was running, create and validate a binary backup, perform replacement, and wrap the update/start sequence in recovery logic that restores the validated backup and restarts the formerly running service when appropriate.

Retain the existing strict data-directory ACL. Change only the application-directory ACL from inheritance removal to enabled inheritance with explicit stable SID grants. Update `docs/deployment/windows-production.md` so operators extract the ZIP, run elevated PowerShell, and understand that downloaded files are unblocked automatically and that application permissions differ from protected runtime-data permissions.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, edit the installer and guide with `apply_patch`. Parse every deployment PowerShell script using the PowerShell parser. Then run:

    PowerShell -ExecutionPolicy Bypass -File .\deploy\windows\Build-Package.ps1

The expected final transcript reports a successful Release build, 61 passed tests with zero failures, and the ZIP path plus SHA-256.

## Validation and Acceptance

Static acceptance requires every deployment PowerShell script to parse without errors and the final diff to contain no unrelated or generated runtime data. Build acceptance requires zero build errors and warnings, 61 passed tests, and a self-contained ZIP containing the corrected installer.

Server acceptance is behavioral: a clean elevated install runs as `LocalSystem`; an update can repair a previously protected application DLL before backup; an administrator can read the installed EXE; `LocalSystem` can start it; the previous binaries are backed up; `C:\ProgramData\CrptWorkbench` remains intact and restricted; and `/health/live` plus `/health/ready` return their documented results. The package is prepared locally; executing these server acceptance scenarios remains an operator action because this workspace is not the target Windows Server.

## Idempotence and Recovery

Path validation occurs before any ownership, ACL, deletion, or replacement operation. Re-running the installer stops only `CrptWorkbench`, creates a new timestamped backup, and never deletes runtime data. If an update of a formerly running service fails after a complete backup was created, the installer restores those binaries and attempts to restart that service. A clean installation failure remains visible for diagnosis rather than pretending an older installation existed.

## Artifacts and Notes

The deliverable is `artifacts\windows\CrptWorkbench-win-x64.zip`.

    SHA-256: A088B284B3622B4F65F9FB0C560E42BC5BDADE57A9BB1D5F65466D2B257D9C0C

ZIP inspection found 411 entries, the corrected `Install-Service.ps1`, `DEPLOYMENT.md`, and zero paths matching `App_Data`, `keys`, database files, PFX/P12 files, or private-key files.

## Interfaces and Dependencies

No application API changes are required. The installer remains compatible with Windows PowerShell 5.1 and uses built-in `icacls.exe`, PowerShell ACL/file APIs, Windows service cmdlets, and `Unblock-File`; it introduces no package dependency.

Revision note (2026-09-15): Created the plan after reproducing the deployment failure and confirming that broken application-directory ACLs were the cause. Updated progress and discoveries after implementing the script/documentation changes and completing PowerShell syntax verification. Closed the plan after the clean Release build, 61-test pass, and final ZIP content inspection.


Revision note (2026-09-18): Archived completed plan; historical implementation and verification evidence retained. Current repository guidance is ../../AGENTS.md.
