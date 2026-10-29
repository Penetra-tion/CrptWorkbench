# Durable aggregation tree implementation

Approved scope: unbounded TXT/paste/TraceX inputs and disk-backed forest; durable pause/manual restart; exact child-to-root TXT with package-type projection and unique final lines; approved cises/info only.

Tasks: (1) streamed transport/parser, (2) SQLite source/graph/queue/export and processor, (3) Console/TraceX/history and lazy tree UI, (4) regression/full tests and Mock browser verification.

Ruling: implement in the current dirty checkout on codex/unified-operations, preserving pre-existing work as requested. No branch creation, commits, push, or production calls are part of this request.

Ruling: graph tables are accessed by a provider-specific SQLite repository behind Domain ports. The additive migration creates these tables without mapping large graph entities into EF tracking; existing EF model remains unchanged.

Progress (2026-10-28): storage, processor, streamed transport/parser, Console/TraceX/history, protected HTTP exports and lazy UI implemented. Version 1.31.0. Web and Release test builds passed with zero warnings/errors. First full Release run: 808 passed, no failures/skips; 100001-node case took 1h14m and exposed excessive per-node I/O cost.

Review fixes: actual RED tests reproduced stranded Fetching claims on manual resume (0/2 loaded) and Descend snapshot truncation (4/5 loaded). Resume now atomically requeues unfinished claims, and expansion uses its snapshot flag. Third finding: job-list paging now keeps its own cursor, independent of an old job opened from History.

Performance RED: 128-node test observed 128 individual metadata writes. Processor now buffers at most one API batch (1000 metadata rows) and stages them in one transaction. Parser creates child spools lazily, so ordinary leaf items create no temporary file. Huge-child token/edge streaming remains bounded.

Browser smoke on isolated Mock: pasted two shared-tree UNIT codes completed with five unique nodes. Found and fixed Razor string-literal bindings for InitialText and child Parent. Need final builds/full suite, runtime refresh, large TXT/pause/restart/download/paging/380px verification and final diff/docs.
