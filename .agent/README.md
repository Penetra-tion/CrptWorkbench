# Agent Workspace

This directory contains repository-specific operating guidance for coding agents and historical execution plans.

## Authoritative Instructions

- [`../AGENTS.md`](../AGENTS.md) is the current source of truth for architecture, integration safety, code conventions, build and test commands, and repository-wide agent behavior.
- [`PLANS.md`](PLANS.md) defines how to create and maintain an ExecPlan for complex features and significant refactors.
- [`SUBAGENTS.md`](SUBAGENTS.md) defines optional specialist roles and their handoff boundaries. It supplements but never overrides `AGENTS.md`.

## Active Plans

There is currently no active ExecPlan in this directory. Place a new active plan directly under `.agent/`, maintain it throughout implementation, and move it to `archive/` after its progress is complete and its outcome is recorded.

## Completed Plans

The files below are historical records. Their test counts, implementation descriptions, safety statements, and next-step language reflect the project at the time each plan was executed. They are not authoritative descriptions of the current repository; consult `AGENTS.md` instead.

1. [`archive/production-ui-get-console.md`](archive/production-ui-get-console.md) — completed 2026-09-14; production-oriented UI terminology, themes, navigation, and GET console behavior.
2. [`archive/code-info-list.md`](archive/code-info-list.md) — completed 2026-09-15 09:21Z; marking-code details and direct package relationships.
3. [`archive/true-api-production-deployment.md`](archive/true-api-production-deployment.md) — completed 2026-09-15 12:10Z; allowlisted production True API transport and Windows Service packaging.
4. [`archive/tracex-special-features-favorites.md`](archive/tracex-special-features-favorites.md) — completed 2026-09-15 15:02Z; TraceX configuration, migrations, placeholder work areas, and operation favorites.
5. [`archive/signedcms-signature.md`](archive/signedcms-signature.md) — completed 2026-09-15 23:59Z; in-process detached PKCS#7 signing with SignedCms.
6. [`archive/windows-installer-acl-fix.md`](archive/windows-installer-acl-fix.md) — completed 2026-09-15; installer ACL repair and verified release packaging (time not recorded).
7. [`archive/serilog-separated-logs.md`](archive/serilog-separated-logs.md) — completed 2026-09-16; separate UI/server file logs and safe diagnostics.
8. [`archive/windows-service-update-recovery.md`](archive/windows-service-update-recovery.md) — completed 2026-09-16; service update, rollback and shared DACL-only implementation.
9. [`archive/signing-safe-diagnostics.md`](archive/signing-safe-diagnostics.md) — completed 2026-09-16; signing stage/HRESULT diagnostics without secret details.
10. [`archive/signing-challenge-file.md`](archive/signing-challenge-file.md) — completed 2026-09-16; signing exact UTF-8 temporary-file bytes and cleanup.

11. [`archive/code-correction-execplan.md`](archive/code-correction-execplan.md) — completed 2026-09-27; code correction rules, optional preflight, signed submission and durable tracking; 307 tests passed.

The index is grouped by recorded completion date; order within a date does not imply implementation order when an exact time was not recorded. Six completed plans were archived on 2026-09-18. Local completion does not imply target-server signing or elevated deployment acceptance.

When archiving a future plan, keep its historical evidence intact, ensure every `Progress` item is resolved, place its final revision note at the bottom, update relative links to `../PLANS.md`, and add it to this chronological index.
