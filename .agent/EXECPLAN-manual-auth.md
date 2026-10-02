# Add protected manual tokens for True API and SUZ

This living ExecPlan follows `.agent/PLANS.md` and must be kept current while implementation proceeds.

## Purpose / Big Picture

An operator can configure True API and SUZ authentication independently. In Manual mode, the operator enters a token once in Settings; it is encrypted per profile and never displayed again. A 401 tells the operator to replace it, without retrying the request. Auto mode retains certificate authentication.

## Progress

- [x] (2026-10-01) Inspected configuration, Settings, token providers, clients, secret store, and dirty worktree.
- [x] (2026-10-01) Added configuration, protected token service, and manual providers.
- [x] (2026-10-01) Added conditional Settings controls, adjusted retry behavior and readiness.
- [x] (2026-10-01) Added focused tests and raised version to 1.23.0; Release suite reached 501 passed, 0 failed. Mock pages returned HTTP 200.
- [x] (2026-10-01) Final Debug and Release builds passed with zero warnings/errors and the task-file diff check found no whitespace errors. Interactive browser input remains unverified because the available browser automation failed to initialize.

## Surprises & Discoveries

- The True API provider is shared with National Catalog; switching it also switches National Catalog authentication.
- Token providers are singletons while `ISecretStore` is scoped, requiring a fresh dependency-injection scope per manual read.
- Existing unrelated changes are present, including `Directory.Build.props` and `appsettings.json`; edit narrowly.
- Browser automation failed to initialize with `failed to write kernel assets`; rendered component tests cover conditional fields and non-disclosure, while live Blazor button clicks remain unverified.

## Decision Log

- Decision: Two independent config enum values default to Auto. Rationale: requested mixed modes and backward compatibility. Date/Author: 2026-10-01 Codex.
- Decision: Store secrets in existing `ISecretStore` under separate profile/system keys. Rationale: existing Data Protection infrastructure already encrypts these values. Date/Author: 2026-10-01 Codex.
- Decision: In Manual mode a 401 is returned once without invalidating or deleting the token. Rationale: only the operator can replace a manual token. Date/Author: 2026-10-01 Codex.

## Outcomes & Retrospective

Manual tokens are stored encrypted and selected by profile/system. The production clients make one request on a manual 401. The Mock smoke instance returned 200 for `/`, `/console`, `/history`, and `/settings` with no unhandled exceptions. The Release test suite passed 501/501. Live CRPT authorization and interactive browser button clicks remain unverified.

## Context and Orientation

`src/CrptWorkbench.Domain/Model.cs` contains integration settings. `src/CrptWorkbench.Web/Program.cs` binds config and registers token providers. `src/CrptWorkbench.Clients/ProductionTrueApi.cs` and `ProductionSuz.cs` obtain automatic tokens and send production requests. `ProductionNationalCatalog.cs` shares the True API provider. `src/CrptWorkbench.Infrastructure/DataProtectionSecretStore.cs` encrypts secrets in SQLite. `src/CrptWorkbench.Web/Components/Pages/Settings.razor` is the operator UI. Mock clients remain selected by the existing integration mode.

## Plan of Work

Add enum-valued `TrueApiAuthMode` and `SuzAuthMode`, validate both at startup, and register automatic or manual providers independently. Add an Application service exposing save, clear and configured status without a read-token UI method; manual providers read the protected secret through a scope. Accept trimmed opaque tokens only, excluding whitespace inside and control characters. In manual SUZ mode validate OmsId and URL while allowing an absent ConnectionId. Place password inputs and buttons in Settings, clear input on save and profile change, and guard late state updates using the selected profile ID. Skip automatic 401 retries in all three clients when their relevant mode is Manual. Adjust readiness and help text. Add focused tests and version bump.

## Concrete Steps

From `D:\Артём\Projects\C#\СZHELPER`, edit only task files and keep this document current. Run `dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false`, then the documented Release test build and `dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed` outside the restricted sandbox. Start a Mock instance and request `/`, `/console`, `/history`, `/settings`; check the server log and UI behavior. Record actual results here.

## Validation and Acceptance

Auto mode still issues and refreshes signed tokens. Manual mode uses encrypted tokens for the matching profile only and performs no auth request. True API and National Catalog share the manual True API token. SUZ uses its own token. Manual 401 leads to one response and a safe replacement message. The saved token is absent from Settings markup, logs and history. Mock remains local. Build and Release tests pass with zero failures; actual live service acceptance belongs on the target environment.

## Idempotence and Recovery

Settings writes replace one protected value; delete is repeatable. No schema migration is needed. Switching back to Auto leaves encrypted manual tokens for a later Manual selection. Do not revert unrelated dirty files.

## Artifacts and Notes

Initial version observed: `1.22.0`. Initial worktree contains unrelated uncommitted changes. Final Debug and Release builds reported zero warnings and errors. Release tests: 501 passed, 0 failed. Mock smoke pages: all four returned HTTP 200. A deliberate `Crpt:SuzAuthMode=Unknown` launch failed with `Crpt:SuzAuthMode must be Auto or Manual.`

## Interfaces and Dependencies

`CrptAuthenticationMode` has `Auto` and `Manual`. `ManualTokenService` handles configured status, save and delete for a profile/system key through `ISecretStore`. `ManualTrueApiTokenProvider` implements `ITrueApiTokenProvider`; `ManualSuzTokenProvider` implements `ISuzTokenProvider`. They create a service scope per read to obtain `ISecretStore`. The production clients retain their existing HTTP allowlists and response contracts.

Plan revision, 2026-10-01: implemented the chosen design and recorded the browser automation limitation and observed validation evidence.
Plan revision, 2026-10-01: finalized verification after the last Settings text edit; both builds and the 501-test suite remained clean.
