# Performance changes — 1.34.0

Implemented the optimization audit accepted on 2026-10-29.

## API requests

- True API token invalidation removes only the rejected token. A delayed 401 cannot discard a replacement cached by another request. Buffered, streaming and National Catalog retries share this behavior; manual authentication remains unchanged.
- A final 401/403 pauses a bulk TXT/TraceX job. Completed requests publish their fragments; unfinished batches remain resumable without being counted as failed. The job retains an actionable HTTP error.
- Service slots remain limited to four. True API admissions are paced at 20 ms (the v733 common limit is 50 requests/second per participant; reference HTML line 618). HTTP 429 establishes a shared service cooldown. Integer/date `Retry-After` values are respected, with 50–250 ms jitter and bounded fallback backoff. Read batches retain at most two HTTP 429 retries; document creation is never retried.
- A pure dispatch predicate interrupts slot/cooldown/authentication waiting when a job pauses or loses permission. The side-effecting predispatch claim runs only at dispatch. Already dispatched business requests retain their original cancellation token and drain normally. Mock buffered and streaming clients enforce the same boundary.
- An unexpected cancellation/timeout before dispatch pauses durable bulk and graph jobs for manual continuation. It does not silently resubmit the unfinished batch; the safe explanation remains visible after the pause completes.
- Code histories and unique document/group details each use a bounded four-request phase. Detail requests for one code can run concurrently; event order, duplicates, shared-document reuse, payment/unknown semantics and partial warnings remain intact.
- Bulk processing replenishes free request slots as batches finish, without waiting for the slowest member of a four-batch wave. Source preparation remains serialized and batch counter publication retains its persistence gate.

## Local reads and graph processing

- History lists select summary columns only. Selecting the default or an explicit record loads complete profile-scoped request/response details.
- Aggregation job polling reads active loaded jobs in groups of up to 500 IDs. Explicit refresh also reloads terminal jobs; loaded order and append paging remain unchanged.
- Graph imports reuse prepared statements, and a batch claim updates the indexed first 1000 pending nodes as a set.
- Graph child expansion fixes the SQLite join order with bounded `Expansion` rows outside indexed parent-edge lookups. `EXPLAIN QUERY PLAN` on the preceding query selected all job edges followed by a scan of `Expansion`, causing quadratic work on wide trees.
- Child pages start from the parent-edge index. Export reuses prepared lookups/inserts and caches at most 4096 ancestors, without retaining all leaves or paths in memory. Snapshot validation, full export, type selection and duplicate suppression are preserved.

## Verification on 2026-10-29

The regression tests were observed failing before the related fixes: delayed 401 produced three sign-ins instead of two; document detail concurrency could not fill four slots; history summary/grouped reads were absent; auth refusal ended bulk work as Partial; a slow batch blocked subsequent work; 429 did not delay other callers; slot waiting blocked pause; Mock streaming omitted the dispatch check; authentication waiting did not observe pause.

The final two regressions also caught unintended resubmission after a simulated predispatch timeout. Both now verify a resumable pause, zero completed rows/nodes before manual continuation, a retained safe explanation and successful explicit continuation.

Release test build and isolated Debug Web build: **0 warnings, 0 errors**.

```powershell
dotnet build tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test --project tests/CrptWorkbench.Tests/CrptWorkbench.Tests.csproj -c Release --no-build --no-ansi --no-progress --output Detailed
dotnet build src/CrptWorkbench.Web/CrptWorkbench.Web.csproj --no-restore -m:1 -p:UseSharedCompilation=false -p:OutputPath='D:/Артём/Projects/C#/СZHELPER/artifacts/performance-audit/mock-web/'
```

The full unfiltered Release run outside the restricted environment passed **879 tests, 0 failed, 0 skipped**, in **1m 32.479s**. No tests were removed, weakened or excluded. `MoreThanHundredThousandSourceRowsAndGraphNodesAreNotTruncated` passed in **51.834s**, including all child pages and the complete 100000-line export. Before the final expansion join correction, an isolated run was interrupted after 5m 23s; its inspected intermediate checkpoint still had 24000 loaded nodes out of 100001. This is evidence of the removed bottleneck, not a complete before/after throughput benchmark.

An isolated local Mock browser session verified a one-code lookup with document loading and paid status; history summary/default selection with the full saved result; masked technical headers; and a prepared aggregation job reaching Completed with 4/4 nodes, automatic list refresh and expanded parent/child branches. Its saved lookup contained seven exchanges, matching correlation IDs, positive duration and `X-CRPT-Workbench-Mock: true` for every exchange; the raw Mock token was absent and `Bearer ********` was present. `/`, `/console`, `/history`, `/settings` returned HTTP 200. Browser warning/error logs and server unhandled-error checks were clear.

Browser verification used Development hosting for build-output static web assets, a separate data directory under ignored `artifacts/performance-audit/`, and a loopback-only address. Real jobs, live Production, live TraceX and browser file upload were not exercised. Production transport cases used local fake handlers in tests. No endpoint allowlist, persistent schema or secret storage was expanded.
