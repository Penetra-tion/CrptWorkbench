# Complete marking-code details and package relationships

This living ExecPlan follows `../PLANS.md`. The sections `Progress`, `Surprises & Discoveries`, `Decision Log`, and `Outcomes & Retrospective` must remain current while implementation proceeds.

## Purpose / Big Picture

Users paste up to 1000 normalized marking or package codes into API Console and receive a readable result for each code. The result must show product identity, status, owner, dates, product group, package kind, shelf life, permit documents, immediate child codes, and a parent package when those fields are present. The request follows True API PDF v515 section 5.1.2, `POST /cises/info`, with a JSON string array body and no `pg` query parameter. Package relationships come only from the current response: the application must not recursively query descendants or ancestors. All exchanges remain local simulations.

## Progress

- [x] (2026-09-15) Implemented the original multiline `POST /cises/info` mock operation, presentation, masking, persistence, and initial 23-test suite.
- [x] (2026-09-15 09:09Z) Re-read `../PLANS.md`, repository guidance, and True API v515 sections 5.1.2 and the packaging/permit-document dictionaries; recorded the revised user decisions.
- [x] (2026-09-15 09:18Z) Extended the request and response contract, deterministic UNIT/GROUP/BOX mock fixtures, and focused tests; Release test build completed with 0 warnings/errors and 38 tests passed.
- [x] (2026-09-15 09:18Z) Expanded the friendly result UI and removed explicit Correlation ID labels while retaining internal propagation; Release Web build completed with 0 warnings/errors.
- [x] (2026-09-15 09:21Z) Updated repository documentation and completed build, automated, runtime, browser, masking, and mock-safety verification.

## Surprises & Discoveries

The existing True API lookup already uses the correct POST array contract, but it still exposes optional `pg`, understands only two unit codes, and stores most fields only in JSON extension data. True API v515 marks `packageType` obsolete and directs clients to `generalPackageType`. Section 5.1.2 returns immediate children in singular JSON field `child`, not `children`; it also says product name and GTIN may be absent for BOX and ATK without indicating an error. Permit document dates can use either an ISO timestamp or `yyyy.MM.dd`, so they must be preserved as strings rather than deserialized exclusively as `DateTimeOffset`.

The Microsoft Testing Platform runner could not create its local named pipe inside the restricted Windows sandbox. Re-running only the documented test command with outside-sandbox permission completed normally. Browser verification also confirmed that raw headers retain `X-Correlation-Id` and `X-CRPT-Workbench-Mock: true` even though the dedicated Correlation ID label is absent.

The surrounding Git repository reports this project as an untracked directory because its root is the parent `D:\Артём\Projects\C#`. Treat all surrounding content as user-owned and inspect only the project files changed by this work.

## Decision Log

- Decision: Remove the `pg` descriptor field and `CodeInfoRequest.ProductGroup`; always call `/cises/info` without a query string while retaining `productGroup` in responses.
  Rationale: The parameter is optional in True API v515 and the user does not want to select it.
  Date/Author: 2026-09-15 / user and Codex.
- Decision: Prefer `generalPackageType`, fall back to obsolete `packageType`, and localize UNIT, GROUP, SET, BUNDLE, BOX, ATK, and LEVEL1 through LEVEL5 while preserving unknown values.
  Rationale: This is the forward-compatible response contract and covers consumer, group, set, bundle, transport, and customs packages.
  Date/Author: 2026-09-15 / user and Codex.
- Decision: Show only immediate `child[]` values and the `parent` string already returned for the requested code; do not make follow-up requests.
  Rationale: The user explicitly reversed the earlier full-tree choice.
  Date/Author: 2026-09-15 / user.
- Decision: Remove explicit Correlation ID labels from API Console and History but retain the value in operation context, outgoing/response headers, persisted history, and raw technical headers.
  Rationale: It is not useful in the friendly UI but remains required for diagnostics and traceability.
  Date/Author: 2026-09-15 / user and Codex.
- Decision: Keep the summary table compact and put dates, permit documents, shelf-life variants, and package relationships in per-code details; auto-open details only for a single requested item.
  Rationale: The endpoint accepts up to 1000 roots, so a wide all-fields table would be unusable.
  Date/Author: 2026-09-15 / user and Codex.

## Outcomes & Retrospective

The enhanced lookup now accepts codes without `pg`, returns and presents UNIT, GROUP, and BOX examples, supports current and legacy package type fields, multiple shelf lives, permit documents, immediate children, and a parent without recursive requests. Dedicated Correlation ID labels are absent from API Console and History while the internal value remains in raw headers and persistence. Both Release builds completed with zero warnings and zero errors; the focused suite completed with 38 passed, 0 failed, and 0 skipped. Browser verification covered single unit, group, box, mixed, and unknown-code scenarios plus `/`, `/console`, `/history`, and `/settings`; no unhandled exception or real network request occurred.

## Context and Orientation

`src/CrptWorkbench.Domain/CodeInfo.cs` holds the shared protocol response records used by Application and Clients. `src/CrptWorkbench.Application/TrueApiCodeInfoHandler.cs` validates input and creates the single mock-safe `/cises/info` request; `CodeInfoResults.cs` parses results and provides presentation labels. `src/CrptWorkbench.Clients/MockClients.cs` creates deterministic local exchanges. `src/CrptWorkbench.Web/Components/Pages/ApiConsole.razor` owns console execution and technical display, while `CodeInfoResultView.razor` renders the friendly code-specific result. `tests/CrptWorkbench.Tests` verifies the contract and persistence. No database schema change is required.

## Plan of Work

First, remove `pg` from the request record, descriptor, URL construction, mock filtering, tests, README, architecture notes, and repository guidance. Preserve the existing validation of 1–1000 nonblank normalized codes, 18–74 characters each, and the single `ITrueApiClient.ExecuteAsync` call.

Next, extend `CisInfo` with `GeneralPackageType`, immediate `Child` codes, `Parent`, permit documents, permit-document absence details, and multiple shelf-life entries. Add immutable records for permit documents, absence details, and shelf-life entries. Keep permit dates as strings and keep unknown JSON extension fields. Add label helpers for package and permit types, using `GeneralPackageType ?? PackageType`.

Update the mock with deterministic `UNIT`, `GROUP`, and `BOX` fixtures. Use `010460000000000021DEMO000000001` and `...002` as units, `046000000000000001` as a group, and `000000000000000001` as a transport box. The box directly contains the group; the group directly contains the two units; the units reference the group as parent and the group references the box. Populate representative dates, shelf-life conditions, and permit documents. Omit product and GTIN for BOX as the PDF permits. Do not recursively include child objects and do not issue child lookups.

Finally, render six summary columns: code, type, product, GTIN, status, and owner. In details show application and emission dates, product group, one or multiple shelf-life values with conditions, all available permit-document fields or absence reason, direct children, and parent. For BOX and ATK, explain that missing product, GTIN, or application date is not returned for that package type. Remove explicit Correlation ID markup from all console states and History while leaving raw headers untouched. Update CSS for the additional summary column and structured document/relationship lists.

## Concrete Steps

Run from `D:\Артём\Projects\C#\СZHELPER`:

    dotnet restore CrptWorkbench.sln -p:RestoreUseStaticGraphEvaluation=true
    dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
    dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed
    dotnet run --project src/CrptWorkbench.Web/CrptWorkbench.Web.csproj

After tests, update this plan and `AGENTS.md` with the exact final passed count. During runtime verification open `/`, `/console`, `/history`, and `/settings`; exercise the unit, group, box, mixed, and unknown-code cases; then stop only the server process started for this verification.

## Validation and Acceptance

Automated tests must prove the request URL never includes `pg`, the descriptor contains only the multiline codes field, configured mock base paths remain safe, and exactly one client call occurs for package input. Response tests must cover all package labels and unknown fallback, legacy `packageType`, preferred `generalPackageType`, immediate children, parent, full permit-document records, absence reasons, a single expiration date, multiple expirations with storage conditions, optional/missing package fields, mixed successes, and errors. Persistence tests must still prove one masked SQLite history row per execution, internal Correlation ID consistency, and absence of original secrets.

The browser must show the six-column summary, automatically open a single result's details, keep list results collapsed, present BOX without a false error when product and GTIN are absent, and list immediate children without extra requests. API Console and History must contain no explicit `Correlation ID` label. Technical headers must still contain the internal `X-Correlation-Id`, and every exchange must contain `X-CRPT-Workbench-Mock: true`. The documented Web build must finish with zero errors and no introduced warnings, and all tests must pass.

## Idempotence and Recovery

All edits are source-only and repeatable. No migration or user database reset is needed. Tests use temporary SQLite files and delete only those files in `finally`. Preserve existing App_Data, keys, build output, and unrelated files. If the Microsoft Testing Platform named pipe is blocked, rerun only the documented test command with the required outside-sandbox permission.

## Artifacts and Notes

True API v515 section 5.1.2 is on PDF pages 360–375. Packaging labels are on pages 851–853. `generalPackageType` is the current field; `packageType` is obsolete. Permit documents use `certDoc`; direct children use `child`; parent uses `parent`; multiple shelf lives use `expirations`.

## Interfaces and Dependencies

`CodeInfoRequest` must contain only `IReadOnlyList<string> Codes`. `CisInfo` remains a Domain record and gains optional fields without introducing project references. Permit-document dates are strings; emission, application, and expiration timestamps remain nullable `DateTimeOffset`. `CodeInfoResults` remains the Application parser and presentation helper. `TrueApiCodeInfoHandler` continues to implement `IApiOperationHandler` and makes exactly one request through `ITrueApiClient`. No external package, database migration, real host, credential, signing behavior, or network adapter is added.

Revision 2026-09-15: replaced the completed initial plan with a self-contained enhancement plan covering removal of `pg`, richer code/package details, direct relationships only, hidden Correlation ID labels, and revised verification requirements.

Revision 2026-09-15 09:21Z: marked the enhancement complete and recorded the 38-test, build, runtime, browser, masking, and mock-boundary evidence.
