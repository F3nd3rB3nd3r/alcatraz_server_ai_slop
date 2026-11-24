# In-game leaderboard review

This branch starts at upstream commit `9eeec3a` and can be built and reviewed on its own.

The database bootstrap commit fixes an upstream migration that queried columns added by later migrations. It selects only the legacy ID and password columns and updates the password property. Fresh SQLite databases and upgrades can initialize the leaderboard fixtures through the production migration path.

- Range reads rank the full board before paging. Friend lookups keep global ranks, multiplayer replies include companion columns, and key-rank replies provide ordered reference rows.
- Score and time writes honor the client's maximum and minimum policies and compare mixed numeric values without rounding large integers. Rejected or empty submissions skip board creation. Valid submissions in the same request still save.
- Reads consistently select the latest logical board for each player and leave missing boards out of storage. A write transaction protects board creation and updates, and the process lock serializes concurrent writes. Historical duplicate records remain available for inspection.
- Game account lookups use in-game nicknames so player details resolve the same identity as leaderboard rows.
- Shared wire serialization preserves 64-bit values and packed dates. The scalar tests cover that change separately.

## Verification

Run these commands from the repository root with the .NET 8 SDK or later.

```powershell
dotnet run --project tests/LeaderboardSimulation -c Release
dotnet run --project tests/ProtocolSerialization -c Release
$fixture = Join-Path artifacts ('database-bootstrap-' + [Guid]::NewGuid().ToString('N'))
dotnet run --project tests/DatabaseBootstrap -c Release -- $fixture
```

The expected totals are **83 leaderboard checks**, **12 scalar checks**, and **three SQLite bootstrap checks**, all passing. The leaderboard checks exercise production services with synthetic accounts and fresh SQLite fixtures under the ignored `artifacts/` directory. They cover scoring, ranking, pagination, response framing, duplicate boards, concurrent writes, rollback, and account lookups. The scalar checks need no database. Both backend frontends can be built with `dotnet build AlcatrazGameServices -c Release` and `dotnet build BackendDebugServer -c Release`.

Two-client tests of the diagnostic-enabled private build verified challenge times and scores, multiplayer columns, removal of duplicate zero rows, player-details names, and the PC **My Position** control. This review includes the same functional fixes and uses the standalone test suite above.

## Remaining protocol questions

The observed PC World and Friend Leaderboard controls work through ordinary range and player-ID requests. A range request with a nonzero filter returns `Core_NotImplemented` until its scope is established. The original Tanner’s Day Off sample uses ranking-criterion index 0, while generated data defaults to 1. The inspected PC screens rank by the requested column and numeric value. Website rankings are outside this review.
