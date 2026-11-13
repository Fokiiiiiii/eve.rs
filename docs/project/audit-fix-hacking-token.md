# Static audit fixes: hacking clicks and redeem token IDs

Audit date: 2026-11-13

## H-08: revealed defense nodes could not be attacked repeatedly

**Verdict: Confirmed.** The defect was in `HackingGame.ClickTile`: one call revealed a hidden tile and immediately ran its gameplay action, then later calls were rejected because the tile was no longer hidden. The client sends `ClickedOnTile` on every mouse click, so a partially damaged defense could not receive the next hit.

### Evidence

- V24.01 client source snapshot `/home/user/eve-logic`, commit `1dbb5711a072a146e1d228365668ac05655eefe1`:
  - `eve/client/script/ui/hacking/hackingTile.py:273-278` forwards mouse-down to `hackingUI.OnTileClicked`; the same widget identifies core and defense-software tiles as attackable.
  - `eve/client/script/ui/hacking/hackingUISvc.py:262-270` sends `hackingMgr.ClickedOnTile(tileCoord)` for each click. It does not suppress the RPC after a tile is revealed.
  - `eve/client/script/ui/hacking/hackingTileData.py:49-54` defines flipping eligibility from neighboring visible, unblocked tiles. It does not define the tile's own `hidden` state as a reason to reject a click.
  - `eve/client/script/ui/hacking/hackingUISvc.py:105-128,137-142` consumes `EVENT_TILE_FLIPPED` to unveil defense software and consumes subsequent `EVENT_ATTACK` updates to refresh the tile and virus state.
- EveJS reference `/home/user/evejs-261108.1`:
  - `server/src/services/exploration/hackingMgrService.js:1004-1010` maps every `ClickedOnTile` RPC to a click action.
  - `server/src/services/exploration/hackingBoardEngine.js:470-508` has distinct hidden-tile reveal and revealed-object actions. A revealed core or defense node loses one hit of coherence on each click (`:501-506`), with retaliation while it survives.
- Before the fix, the new real Host-wire regression test failed: its first click produced `[TileFlipped, Attack, Attack]` instead of only `[TileFlipped]`; the following click was acknowledged without attacking. This reproduced both parts of the report through the registered Host service and the Macho wire path.

### Fix

`dotnet/src/Eve.Hacking/HackingGame.cs:94-142` now treats a hidden tile click as a reveal-only turn, then returns. A later click on a revealed cache, utility, core, or living defense follows that object's action branch. Living defense nodes remain targetable on later turns until coherence reaches zero, even if path accessibility would otherwise reject the now-visible node. Dead defenses and unsupported revealed tile types are acknowledged without changing the board.

The Host-wire regression test `Hacking_reveals_a_defense_then_accepts_repeated_attacks_until_it_is_destroyed_over_host_wire` verifies the reveal notification, two successive attacks, coherence and virus changes, destruction event, and active attempt state. The game, service, and container-site tests were updated to exercise the two-click reveal/attack transition and its settlement ordering.

## H-06: 64-bit redeem IDs were described as 32-bit fields

**Verdict: Confirmed.** Runtime redeem IDs are positive 63-bit values derived from SHA256, but the packed row descriptor declared `tokenID` and `massTokenID` as `DBTYPE_I4`. The encoder checks the signed 32-bit range and rejected valid IDs above `Int32.MaxValue`.

### Evidence

- `dotnet/src/Eve.Host/Persistence/NewEden/PersistenceNewEdenStoreRuntime.cs:145` uses the shared ID generator for daily-login rewards; seasonal login rewards use it at `PersistenceNewEdenStoreRuntime.Part2.cs:176-183`. The generator masks SHA256 output to a positive `long` at `PersistenceNewEdenStoreRuntime.cs:641-647`.
- `dotnet/src/Eve.Services.Core/UserSvcCompatibility.cs:213-225` defines the packed redeem-token descriptor. Before the fix, both token ID fields were `0x03` (`I4`).
- `dotnet/src/Eve.Marshal/PackedRow.cs:451-458` encodes `I8` as 64-bit and rejects `I4` values outside the signed 32-bit range.
- V24.01 client source `/home/user/eve-logic` defines `DBTYPE_I8 = 20` in `carbon/common/lib/const.py:85-91`, and uses `DBTYPE_I8` for ID columns in `eve/common/script/sys/eveCfg.py:605-612`. The redemption call path reads token IDs into sets and sends them back without a 32-bit cast (`eve/client/script/ui/services/redeemsvc.py:48-64,99-103`; `eve/client/script/ui/shared/redeem/redeemItemsContainer.py:350-354`).
- EveJS reference `/home/user/evejs-261108.1/server/src/services/user/userService.js:69-83` declares redeem token IDs as `I4` and its local generator returns small sequential IDs (`:452-462`). EveJS's generator is therefore not evidence that eve.rs's wider SHA256-derived IDs fit the `I4` wire type.
- Before the fix, the new boundary Host-wire test returned an error response instead of a redeem rowset for IDs above `Int32.MaxValue`. The existing database-backed seasonal-login flow also failed while encoding its generated token row with `MarshalException: value out of range: I4`.

### Fix and compatibility choice

`UserSvcCompatibility.Columns` now declares `tokenID` and `massTokenID` as `DbType.I8`. The SHA256 ID generator remains unchanged. This intentionally differs from EveJS's descriptor because eve.rs creates wider IDs; the V24 client supports the `I8` descriptor and its redemption code treats these values as opaque numeric identifiers.

`Redeem_token_ids_round_trip_at_int32_and_int64_boundaries_over_host_wire` exercises the registered `userSvc.GetRedeemTokens` RPC over Host wire and verifies `Int32.MaxValue`, `Int32.MaxValue + 1`, and `Int64.MaxValue` for both token fields. The database-backed seasonal-login test verifies persisted generated IDs above the 32-bit boundary survive rowset marshal/unmarshal.

## Checks performed

Verified locally on 2026-11-13:

- `Eve.Hacking.Tests` `HackingGameTests`: 22 passed, 0 skipped.
- `Eve.Services.Space.Tests` `HackingMgrServiceTests` and `ContainerSiteEndToEndTests`, using `eve_p4`: 26 passed, 0 skipped.
- `Eve.Host.Tests` Hacking-filtered Host-wire tests: 5 passed, 0 skipped.
- `Eve.Host.Tests` selected New Eden store/entitlement, UserSvc, redeem boundary, persistence, and Hacking tests, using `eve_p4`: 31 passed, 0 skipped.
- `Eve.Services.Core.Tests` selected `StoreOffersServiceTests` and `UserSvcServiceTests`: 13 passed, 0 skipped.
- Release builds of `Eve.Hacking`, `Eve.Services.Core`, `Eve.Host`, `Eve.Services.Space`, and their four touched test projects: all succeeded with 0 warnings and 0 errors.

The pre-fix failures above were reproduced before changing the corresponding implementation. The V24.01 source and EveJS reference were inspected; no real graphical-client session or live client trace was run. The EveJS checkout has no Git metadata, so the reference is identified by its supplied directory `/home/user/evejs-261108.1`.
