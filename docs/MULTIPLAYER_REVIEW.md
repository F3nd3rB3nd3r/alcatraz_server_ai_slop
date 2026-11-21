# Multiplayer backend review

This local review branch is based directly on upstream `ReHamster/AlcatrazServer` master commit `9eeec3a9d77ad17203bbd60cae5503bc698ed9bc`. It is intended for a PR **inside the contributor's repository**, comparing its clean baseline with this branch. Nothing is published by this preparation.

The changes correct independently demonstrated backend defects. They do **not** establish the cause or resolution of the original intermittent host/join-order-dependent internet failure. Packet captures, accounts, deployment history, private configuration, the unrelated database migration and the forced-LAN NAT test mode are excluded.

## Commit-by-commit review

Each commit adds its production change and corresponding tests. Later commits depend on the earlier diagnostic/harness foundation; merging the branch preserves those dependencies. Whole-suite results intentionally expose remaining failures during the series. Focused gates must pass; a nonzero full run is never described as all-green.

### 01. Add opt-in network diagnostics and an offline regression harness

Opt-in JSONL diagnostics record connection/packet/ACK/session metadata, excluding payloads, credentials and arbitrary exception text. Disabled diagnostics do not evaluate snapshots. Queue/file bounds and writer failure containment have automatic checks. The console harness uses production methods and loopback UDP, without web/database startup or real accounts. This first commit characterizes defects; it is not a behavior fix.

Gate: `dotnet run --project tests/MultiplayerSimulation -- --baseline`. Baseline characterization matches all 20 observations; 12 diagnostics checks pass. Full snapshot: 20 scenarios, 7 passes, 13 invariant failures, 0 harness errors.

### 02. Isolate reliable replies and acknowledgements by recipient and stream

Two synthetic server calls could share headers and inherit the first recipient endpoint. Reliable cache lookup now includes the intended recipient. Incoming ACK removal additionally matches that connection's outgoing signature, session, sequence and reversed virtual ports, preventing another connection/stream from clearing the response. Wire headers and retry intervals are preserved.

Gate: `dotnet run --project tests/MultiplayerSimulation -- --filter ack-,notification-,cache-replay-,lost-ack-retry-control`. All 22 selected scenarios pass; 12 diagnostics checks pass. Full snapshot: 33 scenarios, 25 passes, 8 invariant failures, 0 harness errors.

### 03. Serialize hosted PRUDP callbacks per service

A timer callback can overlap itself, selecting a pending response twice or sharing ownership of the receive task. A per-service Interlocked guard covers update/retry selection and receive-task consumption, releases in finally, and records busy skips. Tests force reentry and competing callbacks without timing-dependent sleeps; independent services remain independent.

Gate: `dotnet run --project tests/MultiplayerSimulation -- --filter hosted-callback-`. All 4 selected scenarios pass; 15 diagnostics checks pass. Full snapshot: 42 scenarios, 32 passes, 10 invariant failures, 0 harness errors.

## Reproduction and validation boundaries

Tests are bounded synthetic fixtures calling production code, including real loopback ACK/reply handling. They use no live account, authentication server, database, game executable or public backend. Some private timestamps/methods are forced through reflection to reproduce exact interleavings without waiting. All test-file formatting was independently checked for Roslyn syntax equivalence.

A complete new independent network replay/game trial was not performed for each reconstructed commit. The development investigation also replayed seven existing captures (3,207 complete RMC messages, including 26 multipart messages) and completed ordinary two-client LAN compatibility checks. Those results support the tested development-build paths, not arbitrary internet traffic or every reconstructed intermediate commit.
