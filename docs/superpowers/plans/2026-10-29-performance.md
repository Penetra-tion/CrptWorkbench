# API and local performance implementation plan

> **For agentic workers:** Use executing-plans for integration and dispatching-parallel-agents for independent files. Preserve all existing uncommitted work.

**Goal:** Implement the eight optimization findings accepted by the user on 2026-10-29.

**Architecture:** Preserve the Domain/Application/Clients/Infrastructure/Web boundaries. Use shared bounded request coordination, durable job checkpoints, lightweight list reads and set-based SQLite operations. No new external endpoints or automatic creation retries.

**Tech Stack:** .NET 10, Blazor Interactive Server, EF Core/SQLite, xUnit v3.

**Spec:** The audit in this chat: rejected-token-aware invalidation; stop bulk dispatch on final 401/403; shared rate/cooldown handling; bounded independent document reads; optimize graph SQL/export; lightweight history; grouped active-job polling; continuously replenish bulk work.

## Global constraints
- Maximum four active requests per service; preserve Mock default and all allowlists.
- Preserve manual auth semantics, signing serialization and no retry of document creation.
- Pause/cancel must block every new batch/retry; drain dispatched work and preserve duplicates, ordering, fragments and counters.
- Work in this checkout: the accepted implementation depends on the extensive existing uncommitted state that a new worktree would omit. Do not commit unrelated work.
- Tests run outside the restricted sandbox, with explicit execution approval. Coordinate builds to avoid shared-output races.
- Bump 1.33.1 to 1.34.0 once and document exact verification, including any limitations.

## Review focus
- Late 401 must not invalidate a replacement token, including True API streaming and National Catalog.
- Pause during limiter/cooldown waiting must prevent the actual next dispatch.
- Final auth refusal preserves resumable unfinished batches and does not fail all remaining input.
- History selection must load full details even though list records omit heavy bodies.
- Graph changes must retain all 100001 nodes, valid leaf-to-root exports and independent profile/mode access.

## Tasks
- [x] 1. Add deterministic regression coverage, observe failures, implement rejected-token-aware invalidation for True API/NC.
- [x] 2. Add regression coverage, observe failures, flatten independent document detail reads with shared deduplication and a four-request bound.
- [x] 3. Add regression coverage, observe failures, implement lightweight history reads and selected-detail loading; group aggregation-job refreshes; optimize graph batch claims/import/export without unbounded memory.
- [x] 4. Add regression coverage, observe failures, implement service request pacing/shared 429 cooldown with Retry-After parsing and bounded jitter; retain two read retries and creation non-retry semantics.
- [x] 5. Add regression coverage, observe failures, implement rolling bulk workers, durable pause on final auth refusal, and dispatch checks after waiting.
- [x] 6. Integrate, review diff, bump version, build Debug Web and Release tests, run focused/full tests including the large graph, run isolated Mock/core route checks, inspect safe logs and write verification notes.

## Progress and rulings
- User has explicitly asked to implement the preceding concrete audit; proceed without another design approval round.
- Existing dirty checkout is the implementation baseline. No checkout/reset/stash/commit operation is part of this task.
- Review found limiter/auth waits must observe the pure eligibility predicate while waiting, and Mock streaming must invoke the dispatch callback. Added failing regressions, fixed both, and received no blocking findings in final read-only review.
- A real 100001-node run exposed quadratic SQLite expansion join order. EXPLAIN confirmed edges outer scan / Expansion inner scan; changed to Expansion CROSS JOIN indexed edges. Full879-test final run passed in1m32.479s with large graph51.834s.
- Final evidence and explicit verification limits are recorded in docs/performance-1.34.0.md.
- Final review identified unrequested predispatch cancellation could be silently rescheduled. Two regressions failed before the fix, then passed after conditional manual pause was added; graph pause retains job.Error. The final unfiltered suite contains879 tests, all passed.
