# Multiplayer backend review

This local review branch is based directly on upstream `ReHamster/AlcatrazServer` master commit `9eeec3a9d77ad17203bbd60cae5503bc698ed9bc`. It is intended for a PR **inside the contributor's repository**, comparing its clean baseline with this branch. Nothing is published by this preparation.

The changes correct independently demonstrated backend defects. They do **not** establish the cause or resolution of the original intermittent host/join-order-dependent internet failure. Packet captures, accounts, deployment history, private configuration, the unrelated database migration and the forced-LAN NAT test mode are excluded.

## Commit-by-commit review

Each commit adds its production change and corresponding tests. Later commits depend on the earlier diagnostic/harness foundation; merging the branch preserves those dependencies. Whole-suite results intentionally expose remaining failures during the series. Focused gates must pass; a nonzero full run is never described as all-green.

### 01. Add opt-in network diagnostics and an offline regression harness

Opt-in JSONL diagnostics record connection/packet/ACK/session metadata, excluding payloads, credentials and arbitrary exception text. Disabled diagnostics do not evaluate snapshots. Queue/file bounds and writer failure containment have automatic checks. The console harness uses production methods and loopback UDP, without web/database startup or real accounts. This first commit characterizes defects; it is not a behavior fix.

Gate: `dotnet run --project tests/MultiplayerSimulation -- --baseline`. Baseline characterization matches all 20 observations; 12 diagnostics checks pass. Full snapshot: 20 scenarios, 7 passes, 13 invariant failures, 0 harness errors.

## Reproduction and validation boundaries

Tests are bounded synthetic fixtures calling production code, including real loopback ACK/reply handling. They use no live account, authentication server, database, game executable or public backend. Some private timestamps/methods are forced through reflection to reproduce exact interleavings without waiting. All test-file formatting was independently checked for Roslyn syntax equivalence.

A complete new independent network replay/game trial was not performed for each reconstructed commit. The development investigation also replayed seven existing captures (3,207 complete RMC messages, including 26 multipart messages) and completed ordinary two-client LAN compatibility checks. Those results support the tested development-build paths, not arbitrary internet traffic or every reconstructed intermediate commit.
