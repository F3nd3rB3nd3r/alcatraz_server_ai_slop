# Friends and block lists

## Retail PC freeze on multiplayer startup

The tested retail Win32 release of Driver San Francisco can freeze on entering multiplayer with both a saved friendship and a block of a different player. The client acknowledged the server’s block-list reply immediately before the freeze. In the startup code, the lookup repeats the comparison against the same blocked ID when it differs from the friend ID. That path fails to advance the block-list iterator.

The freeze reproduced on two machines. Clearing the affected blocks let both clients enter multiplayer again. With `DriverSF_BlockListFix.asi` loaded, both clients fully relaunched and entered multiplayer with a friend and a block still saved. The patched desktop also fetched the World leaderboard in the same session.

## Client fix

The ASI advances the iterator after a mismatch. It checks the executable hash and the lookup instructions before hooking the function. The verified retail Win32 `Driver.exe` SHA-256 is `C79987B0548F7130337C3F295C8FE269E120CEDDAD55C1159E6F5CBC6AA26616`.

Close the game and copy [DriverSF_BlockListFix.asi](../tools/DriverSF_BlockListFix/prebuilt/win32/DriverSF_BlockListFix.asi) to the folder scanned by your existing 32-bit ASI loader, such as `plugins`. Launch the game. The `DriverSF_BlockListFix.<pid>.log` beside the plugin reports whether the hook was enabled. To verify the fix in-game, keep one friend and block a different player, fully exit, then relaunch and enter multiplayer.

The included ASI has SHA-256 `8B5B641BAD19F5B446355EFFCB0343EE2E386661D00DEF10A0288415B47C51E8`. To build the ASI and run its lookup tests, use a Visual Studio Developer PowerShell with the `v145` Win32 C++ toolset and Windows SDK. [MinHook license](../tools/DriverSF_BlockListFix/third_party/minhook/LICENSE.txt)

```powershell
msbuild tools/DriverSF_BlockListFix/DriverSF_BlockListFix.vcxproj /p:Configuration=Release /p:Platform=Win32
msbuild tools/DriverSF_BlockListFix/DriverSF_BlockListFix.vcxproj /p:Configuration=Release /p:Platform=Win32 /p:BlockListSelfTest=true
.\artifacts\driver-block-list-fix\bin\DriverSF_BlockListChecks.exe
```

The Win32 self-test covers empty lists, mismatches, matches at different positions, malformed lists, and repeated calls. The retail relaunch check used the same lookup code on both machines.

## Backend behavior

- Blocks persist in the existing relationship table. Blocking ends any friendship or pending request between the pair, and either player can keep their own block. Unblocking removes the caller’s block. World leaderboard rows remain visible.
- Only the recipient can accept or decline an incoming request. Acceptance notifies both connected players, and retrying an already successful acceptance sends no duplicate notification. Decline affects only a pending request.
- The PC Friends menu fetches up to 20 entries. The server prioritizes incoming requests, then online friends. The menu can omit accepted friends beyond the first page even though the database retains them. An outgoing request is omitted from the sender’s list until it is accepted.

The live two-client test removed an existing friendship, declined a fresh request, then accepted a second request. Both Friends lists showed the restored friendship. The wire checks use production services, loopback UDP, and a fresh SQLite fixture. They cover block ownership and responses, a leaderboard read after blocking, notification recipients, invalid request actions, offline acceptance, and the full 20-entry page.

```powershell
dotnet run --project tests/FriendsBlockList -c Release
dotnet run --project tests/DatabaseBootstrap -c Release -- artifacts/database-bootstrap-review
```

This branch shares the fresh SQLite bootstrap fix with the leaderboard review. It also carries the recipient and per-call cache changes from the multiplayer review, which keep consecutive friend notifications from sharing a send queue.

## Remaining client and policy findings

- An open leaderboard player-details card can retain **Add as Friend** after acceptance. Reopening the card refreshes the action.
- The executable has a numeric-ID Add Friend path. The service method remains a stub, and its retail menu entry point is unverified. The tested menu actions send Add Friend by Name.
- Whether party invitations should respect the FriendsService block list remains a maintainer decision.
