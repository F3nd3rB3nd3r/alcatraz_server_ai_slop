# Multiplayer regression harness

The harness calls production backend methods with bounded synthetic inputs and loopback UDP. It does not authenticate real accounts, start the web/database backend, emulate NAT or run Driver.exe. Run it sequentially because player/session registries are static. It needs .NET 8 tooling and the repository's existing dependencies; no test-framework dependency is added.

From the repository root:

```powershell
dotnet run --project tests/MultiplayerSimulation -- --filter ack-
dotnet run --project tests/MultiplayerSimulation -- --verify-diagnostics
```

`--filter` accepts comma-separated scenario-name prefixes. The selected correctness gate returns 0 only when every selected invariant passes; no matches is an error. `--output artifacts/result.json` writes observations. The unfiltered command reports all observations, including pending defects during the commit series and exploratory client-contract assumptions; a nonzero result must not be presented as an all-green test run. See [review notes](../../docs/MULTIPLAYER_REVIEW.md) for the exact focused gates and full-suite results.

`--baseline` preserves original per-scenario expectations from the investigation, not a moving expectation that every current case should fail. It is for historical characterization and is not the post-fix gate. Some fixtures force private retry/receipt timestamps with reflection; changes to those private names fail visibly. The CONNECT ticket parser remains the production stub, so synthetic CONNECT tests are not authentication validation. Buffer budget fixtures reserve logical payload bytes using shared bounded arrays rather than allocating a memory-exhaustion workload.

All fixture inputs are synthetic. Captures, account data, deployment helpers, private configuration and runtime logs are excluded from the review branch. Formatting rules expand blocks and separate statements; syntax equivalence is checked during review preparation.

For the complete reviewed result in one command, run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/MultiplayerSimulation/Verify-Reviewed.ps1`. It rejects any different failure set, harness error or incomplete diagnostics run. The three exploratory observations remain explicit; it does not change the correctness runner or relabel those observations as passes. Use `-NoRestore` only after dependencies are restored.
