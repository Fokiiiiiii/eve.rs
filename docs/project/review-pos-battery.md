# POS battery control and target lock review

Scope: the POS battery control and target lock work committed on `haiku4` as `110cdc73a`, `25a523d81`, `058ec7a69`, `cee8b80f7`, `f2f0de8ac`, `bf86f0d54` and `0a4255e6f`. It covers migration `0033_starbase_battery_control.sql`, `PgDeployedStore`, `SpaceContainers.PosMgr.cs`, `SpaceContainers.Batteries.cs`, `SpaceContainers.ControlTowers.cs`, `SpaceSessions.cs` and `SpaceSessions.Batteries.cs`, the dogma `AddTargetOBO` / `RemoveTargetOBO` path, the Activate and Deactivate path, `crates/eve-space/src/targeting/battery.rs`, and the SimLink battery messages in `crates/eve-space/src/link` and `crates/eve-space/src/sim`.

Method: each suspected defect was traced from a real caller (a PosMgr or dogma call, a logoff, a fuel job, a tower transition) to the outcome. A confirmed defect got a Host test that was run and seen to fail for the stated reason before the fix; the fix was then run and the test seen to pass. One commit per defect.

## Confirmed and fixed

### F1. A battery's control grant survived its tower going offline
- Path: the hourly fuel job (or an unanchor) runs `OfflineStarbaseModulesAsync` in the tower's transaction. It turned the battery offline, and `OfflineBatteriesAsync` then placed it offline in the simulation with no holder. The `starbase_battery_control` row was not touched.
- Outcome: once the tower and battery were online again, the former holder's `AddTargetOBO` passed the holder check and placed the battery online again without `AssumeStructureControl`. Meanwhile any other pilot was refused as "controlled by another pilot".
- Test: `Battery_control_is_released_when_its_tower_runs_out_of_fuel_so_the_old_holder_cannot_order_it_back_online`. Before the fix: `Assert.Throws` failed with "No exception was thrown" (the order reached the simulation).
- Fix (`16b968eb9`): `PgDeployedStore.ClearBatteryControlAsync` deletes the grant in the same transaction that turns the battery offline.

### F2. Relinquishing a grant or the pilot leaving left the simulation naming the old holder
- Path: `RelinquishStructureControl` calls `ReleaseBatteryControlAsync`, which deletes the row and nothing else. A logoff reaches `OnCharacterLeavingAsync` and then `ReleaseBatteryControlsOfCharacterAsync`, which also deletes rows only. The simulation was never told.
- Outcome: the battery kept the departed session as its holder. A pending lock started by that pilot completed later and sent `OnTargetOBO add` to a pilot that had given control up, and locks it held stayed on the battery.
- Test: `Battery_control_relinquished_or_left_by_its_pilot_tells_the_scene_it_has_no_holder`. Before the fix: `Assert.Equal` failed, the last placement named holder 1 where 0 was expected.
- Fix (`0f0e52a55`): `ClearBatteryHolderAsync` places the battery with no holder under the battery's gate, only when no grant exists at that point. `ReleaseBatteryControlsOfCharacterAsync` now returns the released battery ids (`RETURNING`).

### F3. A battery order did not re-check the holder's access
- Path: `AddTargetOBO` goes through `DogmaIMService.BatteryOrderAsync` and `SpaceSessions.BatteryOrderAsync` to `BatteryTargetingAsync`. That path checked only the holder's character id and the battery's state. The dogma bound-context check compares session ids only, so a holder still reaches the order after moving system or leaving the owning corporation.
- Outcome: such a holder could lock targets for a battery it no longer had access to. The simulation placed the battery in its system with the holder session in another system.
- The assume path already checks the owning corporation, the system and being in space (`RequireControlTowerAccessAsync`); the order path did not.
- Tests: `Battery_order_is_refused_once_its_holder_is_in_another_system_than_its_battery` and `Battery_order_is_refused_once_its_holder_has_left_the_owning_corporation`. Before the fix (with the fix temporarily disabled): both failed with "No exception was thrown".
- Fix (`27cb1620d`): `BatteryOperatorInPlace` requires the holder's session to be in space, in the battery's system and in its owning corporation.
- Note on the test: the first version set attributes on the source session after the Host wire had copied its attributes, so the order never saw the change and the test checked nothing. The tests now set them on `Holder.Session`, the session the wire actually uses, and were re-checked as described above.

## Rejected

- R1. A tower going offline outside the synchronize path leaves a battery online in the simulation. Rejected. The fuel job and the unanchor path both run `SynchronizeControlTowerSlimStateAsync` after commit (`SpaceComposition.cs` 553 and 567; `SpaceContainers.ControlTowers.cs` 666 and 677), and the sweep runs there. The disruptor path (`SpaceContainers.Disruptors.cs` 493) turns an anchoring tower offline, and an anchoring tower has no online battery.
- R2. Codec mismatch between Rust and C#. Rejected. Field order was compared field by field: C# encode (`SimCodec.Encode.cs` 1525 to 1541) against Rust decode (`decode_message.rs` 2773 to 2817), and C# decode (`SimCodec.cs` 1336 to 1380) against Rust encode (`payload.rs` 1029 to 1066). Kinds 168 to 171 match in `kind.rs` and `SimCodec.Shared.cs`. See test-quality note T1 for what the tests can and cannot catch.
- R3. Two pilots taking control at once. Rejected. `AssumeStructureControlAsync` locks the deployed row with `LockAndGetAsync`, checks the holder under that lock and inserts with `ON CONFLICT (deployable_id) DO NOTHING`. The second pilot is refused as controlled by another pilot.
- R4. A relinquish racing a battery's own order. Fixed in FU-R4 (see the follow-up section): the grant is checked again after the scene answers, and a lock of a pilot that no longer holds the grant is taken back. The original text follows. Not counted, residual risk. `BatteryTargetingAsync` reads the holder before taking the battery gate, so a relinquish between the read and the simulation call can re-place the old holder as the scene's holder. The window is a few milliseconds and the next order of the new holder places the right holder again. There is no deterministic seam for a test without adding one, so this is recorded, not fixed.
- R5. A pilot docking keeps the grant. Fixed in FU-R5 (see the follow-up section): docking and jumping release the grant like a logoff. The original text follows. A question for the reference. Docking does not release the grant (the space session closes through `OnSessionClosedAsync`, which does not touch batteries), so the scene's holder session is stale until the pilot orders again or relinquishes. Whether control should end on docking is not settled in the project docs, so this needs a decision, not a code change here.
- R6. A ship change affects control. Rejected. The order path does not read `shipid`. The bound context is keyed by session id and bound object, and the grant is per character, so a ship change neither grants nor removes control.
- R7. Notifications reaching the wrong session of the same character. Not counted. The holder is the session of the pilot's last order. No reproduction is possible in the test rig without a second live session of the same character.
- R8. Host restart. Fixed in FU-R8 (see the follow-up section at the end). The original residual-risk text follows. Grants are in the database and survive a restart. Simulation holder session ids go stale after a restart and are replaced at the next order. If the host crashes between a tower's commit and the post-commit offline placement, the simulation keeps the battery online until its next placement, and a lock already in flight could still complete. The durable fix is a startup sweep over towers that are offline, which is a design decision and was not made here.
- R9. Integer and id parsing. Rejected. `IdOf` and `ReadPositiveId` refuse values of zero or less. A zero target is refused as `TargetingAttemptCancelled` (`Battery_obo_refuses_an_invalid_target...`). Numbers for a battery are checked as finite in both codecs.
- R10. Rust `battery.rs`. No defect confirmed. `go_offline` and `release_all` read the previous holder before replacing it, so the holder told is the right one. Holder change, offline, lock limit, range and settle each have a Rust test in `targeting/tests.rs`.
- R11. Repeated offline placement. Rejected as a defect, noted for efficiency. Each synchronize call places every offline battery of the tower offline again. The simulation does nothing for a battery that is already offline and holds no lock.
- R12. A simulation that is unavailable during the offline sweep. Rejected. The failure is logged. A simulation that is down holds no battery state, so nothing is left behind once it is back.
- R13. Activate and Deactivate on a battery. Not traced in this review. The dogma `Activate` and `Deactivate` handlers take a fitted module of the ship, and the battery's id is a starbase module id, so the expected outcome is a refusal through the ship's module lookup. This has no test and was not confirmed, so it is an open item.

## Test-quality findings

- T1. `crates/eve-space/src/link/battery_tests.rs` (`battery_frames_roundtrip_byte_for_byte`) and `dotnet/tests/Eve.Space.Tests/BatteryCodecTests.cs` each round-trip within one language. Neither would catch a field-order mismatch between Rust and C#. The field order was checked by reading (R2). A shared golden-bytes test is the missing piece. Not added: it needs a fixture, and the instructions were not to run fixture generators.
- T2. The Host rig's `PosMgrHostWire.StartAsync` copies a fixed list of session attributes (`corpid`, `solarsystemid`, `solarsystemid2`, `shipid`, `isCEO`, `corprole`, `role`, `allianceid`) into the wire's session when it opens. Attributes set later on the source session do not reach the calls. The gap is in the test rig, not in the code, but a test that sets an attribute on the source session can pass without testing anything.
- T3. No Host test covered relinquish, the pilot leaving the cluster, or the tower going offline while its battery was held, before this review. The tests added here cover all three.

## Test runs

- Before any fix: `Eve.Services.Space.Tests`, filter `ControlTowerHostTests`: 4 failed, 17 passed, 21 total. The 4 failures are the new tests (`EVE_PG_URL` set to `eve_p4`).
- After each fix, a filtered run passed: `Battery_control_is_released...` 1 of 1; `Battery_control...` 3 of 3; `Battery_order_is_refused...` 2 of 2 with the access fix disabled failing and enabled passing.
- After the last fix: `ControlTowerHostTests` 21 of 21 passed.
- Full `Eve.Services.Space.Tests` project after the last fix: 1271 passed, 0 failed, 0 skipped, 1271 total (11 min 17 s).

## Not run

- The Rust crate was not built or tested in this review, because no Rust file was changed. Rust tests were not run for this report.
- No fixture generator was run, and no SimLink kind was added.

## Follow-up: the open items, decided and implemented

Each item below has its own commit. A failing test was written and seen to fail for the stated reason before its fix.

### FU-R8. A host restart left grants and the simulation's holder stale (R8, fixed)
- Decision: a startup sweep in the restore path. `SpaceContainers.RestoreDeployedAsync` first runs `SweepBatteryControlsOnceAsync`, which runs once per composition and deletes the `starbase_battery_control` rows granted before the composition started. A row granted after that belongs to a pilot of this host and is kept. The sweep is not "every row": `RestoreDeployedAsync` also runs on every system entry (`SystemEntered`), so deleting every row could remove the grant of a pilot that was just given it.
- Then each restored online battery that has no grant is placed in the simulation with no holder (`RestoreBatteryHolderlessAsync`, through `ClearBatteryHolderAsync`). A battery with a grant is left alone, because its holder's order placed it with that holder.
- Test: `A_host_restart_leaves_no_battery_grant_and_places_the_online_battery_with_no_holder`. Before the fix: `Assert.Null` failed with the grant still held (`140000001`). After the fix it passes, and a second restore leaves the battery holderless and grant-free.
- Test run: `ControlTowerHostTests` 22 of 22 passed.

### FU-R5. A holder that leaves space kept its grant (R5, fixed)
- Decision: the release a logoff makes is reused. `ReleaseBatteryControlsOfLeavingPilotAsync` calls the composition's `ReleaseBatteryControl` (which is `ReleaseBatteryControlAsync` of the containers: the rows are deleted and each battery is placed with no holder, so the scene clears the former holder's locks). `OnCharacterLeavingAsync` now calls it through the same helper.
- The helper is called after a successful dock (`FinishDockingAsync`, which covers stations and structures, because `DockStructureAsync` only sends the dock command and the pilot leaves space on `SimDocked`) and after a successful jump to another system (`FinishJumpAsync`, on `SimJumped`). A refused dock or jump releases nothing.
- Not covered, and still open: the other places where a pilot's entry leaves `_pilots` without a logoff, a dock or a jump (`SpaceSessions.Death.cs`, `SpaceSessions.Abyssal.cs`, `SpaceSessions.AbyssalArtifacts.cs`, `SpaceSessions.Ships.cs`). Each one needs its own decision about whether that exit ends control, so they are not changed here.
- Tests: `A_holder_that_docks_gives_up_its_battery_grant_so_the_scene_tells_the_battery_it_has_no_holder` and `A_holder_that_jumps_to_another_system_gives_up_its_battery_grant_so_the_scene_tells_the_battery_it_has_no_holder`. Before the fix: both failed with the released list empty (the dock did leave space, so the grant was kept). After the fix both pass.
- The rig did not wire `ReleaseBatteryControl` (only the production composition does), so the tests wire it to record the release.
- Test run: `ControlTowerHostTests` 24 of 24 passed.

### FU-R4. A relinquish racing a holder's lock order (R4, fixed)
- Decision: the grant is checked again after the simulation call returns, as the review asked. The order is the holder's, placed under the battery's gate. If the grant is no longer the holder's, the order's lock is taken back (a `Remove` for the same target, sent only when the scene did not refuse the lock) and the battery is placed with no holder, so the scene clears the former holder's locks. The order is refused with `NO_CONTROL_GRANT`, which the pilot sees as `DeniedShipChanged`, as for any refused order.
- The race is now deterministic in the fake simulation, so a test was possible. The fake's `PlaceBatteryHook` holds the holder's placement inside the battery's gate. The test starts the order, waits until it is held, then relinquishes over a second host wire of the same pilot (the first wire is busy with the order until the scene answers). It waits until the grant is gone, releases the placement, and then checks the order and the scene.
- Test: `A_relinquish_made_while_the_holders_lock_order_is_in_the_scene_takes_the_lock_back_and_refuses_the_order`. Before the fix: `Assert.ThrowsAsync` failed with "No exception was thrown" (the order was accepted for a pilot that had given control up). After the fix it passes: the lock is ordered and taken back, the last placement has no holder, and the order is refused.
- Test run: `ControlTowerHostTests` 25 of 25 passed.
