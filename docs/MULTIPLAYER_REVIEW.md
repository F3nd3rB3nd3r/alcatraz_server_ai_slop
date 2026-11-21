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

### 04. Resolve alternate NAT targets through unique current RVCID bindings

Probe targets can advertise an alternate endpoint while still identifying a current peer by RVCID. Exact endpoint lookup remains first; a unique eligible current binding supplies fallback only when needed. Ambiguous, stale, ineligible and malformed targets are rejected without misrouting. This resolves routing lookup, not internet NAT traversal. No relay or synthetic LAN target injection is included.

Gate: `dotnet run --project tests/MultiplayerSimulation -- --filter nat-probe-`. All 36 selected scenarios pass; 18 diagnostics checks pass. Full snapshot: 73 scenarios, 65 passes, 8 invariant failures, 0 harness errors.

### 05. Isolate pending fragment ownership and transport cleanup

One handler previously mixed fragmented messages from different clients, acknowledged them to the current callback client and cleared unrelated pending data. Pending buffers are isolated by transport object, signature, session and both virtual ports. Dropping a transport discards only its buffers. Tests interleave 2/5/8 owners and verify serialized RMC replies and ACK recipients.

Gate: `dotnet run --project tests/MultiplayerSimulation -- --filter fragment-,interleaved-fragment-,ordered-fragment-`. All 23 selected scenarios pass; 21 diagnostics checks pass. Full snapshot: 94 scenarios, 87 passes, 7 invariant failures, 0 harness errors.

### 06. Preserve player registration on repeated CONNECT and retire stale aliases

Repeated CONNECT previously dropped the same PlayerInfo and removed it from the registry while reattaching it to the transport. Repeated association is now idempotent. Rebinding detaches the former current alias; account replacement drops only a genuinely current prior owner. Ticket parsing/authentication behavior is unchanged. Synthetic ticket-stub tests are not authentication validation.

Gate: `dotnet run --project tests/MultiplayerSimulation -- --filter connect-,duplicate-connect-`. All 23 selected scenarios pass; 24 diagnostics checks pass. Full snapshot: 116 scenarios, 110 passes, 6 invariant failures, 0 harness errors.

### 07. Scope party event and endpoint cleanup to the departing player

An unrelated participation update could delete a newly created empty party before its owner joined, causing later invites to find no lobby. Event cleanup also consulted the wrong membership set and removed unrelated empty records. Cleanup now targets actual former memberships; explicit owner-drop cleanup retires pending creations, including creations before participation. Endpoint cleanup compares address and port, preserving another peer sharing an address. The datastore cleanup hook is registered at creation.

Gate: `dotnet run --project tests/MultiplayerSimulation -- --filter cleanup-,gathering-create-,unrelated-empty-session,same-ip-different-port-cleanup,different-ip-cleanup-control,disconnect-cleanup-grace-control`. All 53 selected scenarios pass; 24 diagnostics checks pass. Full snapshot: 174 scenarios, 165 passes, 9 invariant failures, 0 harness errors.

## Reproduction and validation boundaries

Tests are bounded synthetic fixtures calling production code, including real loopback ACK/reply handling. They use no live account, authentication server, database, game executable or public backend. Some private timestamps/methods are forced through reflection to reproduce exact interleavings without waiting. All test-file formatting was independently checked for Roslyn syntax equivalence.

A complete new independent network replay/game trial was not performed for each reconstructed commit. The development investigation also replayed seven existing captures (3,207 complete RMC messages, including 26 multipart messages) and completed ordinary two-client LAN compatibility checks. Those results support the tested development-build paths, not arbitrary internet traffic or every reconstructed intermediate commit.
