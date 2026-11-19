# POS force field: adversarial correctness review (read-only)

Date: 2026-11-19. Branch: haiku5. Scope: commits 20da67759, beface500, 92b8cf73c, 2057d6ff5, be7d86606, df9928d1b and migration `0034_force_field_kind.sql`.

Method: read-only. The code was read with Serena and codebase-memory, and the EVE.js reference was read with the Read tool at `/home/user/evejs-261108.1` (`server/src/services/starbase/starbaseAccessRuntime.js` and `starbaseForceFieldBoundary.js`). Nothing was built and no test was run (disk is tight). Every claim below is from reading the code. "Confirmed" means the path was traced from a real caller to the wrong outcome. It does not mean a test was observed failing. Line numbers are as of branch `haiku5` at this review.

## Fix batch on branch haiku5 (F1, F2, F3, F4, F5, F6, F8)

Each fix has its own commit, with the failing test first. Status by finding:

- **Fixed:** F1 (restart re-sends the saved access and the pilot password matches), F2 (a gun reload keeps the credential), F3 (a tower's reconcile is serialized from its commit to its send; the no-item removal retry is not done), F4 (a pilot's match and the system rematch are ordered), F5 (the Host path already existed; the simulation's respec now refreshes the harmonic), F6 (a refused placement takes its item back; the restore goes on past a refusal; the pinned test is corrected), F8 (unanchor completion takes the field out of the simulation; the item half was not reproducible).
- **Not in this batch:** F7 (alliance membership is not re-read live), F9 (the Rust cancel rule), F10 (the settle-path test gap and the restore test gap, partly closed by the F1 and F6 tests), F11 (non-positive ids in the codecs).
- **Protocol:** unchanged. No SimLink kind was added. The `SetPilotAffiliation` message the review proposed for F5 was not added.
- **Final checks:** full `Eve.Services.Space.Tests` (Debug): 1278 passed, 0 failed, 0 skipped. `cargo test -p eve-space --lib`: 1094 passed, 0 failed. Release build with `-warnaserror`: `Eve.Services.Space` and `Eve.Services.Space.Tests` both 0 warnings, 0 errors. A Release `-D warnings` build of `eve-space` was not run (about 2 GB free disk).

## Ranked summary

| # | Severity | Verdict | Finding |
|---|----------|---------|---------|
| F1 | High | Confirmed | A simulation restart drops tower access and every pilot's password match, and the Host never re-sends them. Owner-corporation ships are locked out. |
| F2 | High | Confirmed | `SpaceSessions.AdjustAsync` drops a pilot's ship-password credential on a gun reload, but the simulation keeps the admission. A later tower password change never revokes it. |
| F3 | Medium | Confirmed | `ReconcileForceFieldAsync` sends to the simulation after its transaction commits and outside the row lock. Concurrent syncs can leave a field in space with no item, and the offline path never removes it. |
| F4 | Medium | Confirmed | A ship-password match that started before a tower password change can finish after the rematch and overwrite it with a stale `Matched = true`. |
| F5 | Medium | Confirmed | A pilot's corporation or alliance is a snapshot taken at entry. No message updates it, so a pilot who leaves the admitting group stays admitted. |
| F6 | Medium | Confirmed | A refused placement leaves the committed item behind and throws after the commit. In `RestoreDeployedAsync` that throw skips every later record in the system. |
| F7 | Low-Medium | Confirmed | Alliance admission uses the tower record's `AllianceId`, not live alliance membership as EVE.js does. Nothing re-sends access when membership changes. |
| F8 | Low-Medium | Partially confirmed | Unanchor completion deletes the tower record. If the offline sync failed after its commit, the field and its item survive with no record left to reconcile them. |
| F9 | Low | Confirmed | The Rust cancel rule diverges from EVE.js for `Goto`, `Follow` and `Orbit`. |
| F10 | Low | Confirmed (test gap) | Tests call the boundary directly, not the production settle path. There is no test for restore reconciliation. One refusal test enshrines the stranded item. |
| F11 | Low | Confirmed | Rust and C# accept non-positive `field_id` and `tower_id`. A tower id of 0 would emit harmonic 0, which the C# decoder refuses. |

Rejected candidates are listed at the end, with reasons.

---

## F1 (High, confirmed): a simulation restart loses access and passwords, and the Host never re-sends them

**Outcome (fixed):** the two restart tests `A_restarted_simulation_*` in `ControlTowerHostTests.cs` failed first (access and password match not re-sent; `Assert.Single` on an empty collection). `RestoreDeployedAsync` now re-sends the tower's saved access (`SendSavedForceFieldAccessAsync`, `SpaceContainers.PosMgr.cs`) and rematches the system's ship passwords once after the restore loop. Verified: `ControlTowerHostTests` 23 of 23 passed.

**Files**
- Rust state, in memory only: `crates/eve-space/src/scene/mod.rs:917-921` (`force_field_access`, `force_field_passwords`, `force_field_harmonics`).
- Host restore: `dotnet/src/Eve.Services.Space/SpaceContainers.Deployed.cs:84` (`RestoreDeployedAsync`), `:121-127` (the control tower branch). It places the tower and calls `ReconcileForceFieldAsync`, but nothing else.
- Only one caller of `SetForceFieldAccess` exists: `dotnet/src/Eve.Services.Space/SpaceContainers.PosMgr.cs:185`, inside `SetTowerPasswordAsync`.
- Only one caller of `SetShipForceFieldPassword` on password entry: `SpaceSessions.cs:205` and `SpaceSessions.ShipPasswords.cs:47` (`RematchShipPasswordsAsync`).
- Loss handlers do not re-send: `SpaceContainers.cs:106` (`Forgotten` only clears `_placed`), `SpaceSessions.cs:1453` (`OnLost` only touches structure observers).

**Caller path.** The simulation process restarts. A pilot enters the system, which fires `SystemEntered` (`SpaceContainers.cs:692-695`). That runs `RestoreDeployedAsync`, which places the tower and its field. `Scene::enforce_force_fields` (`crates/eve-space/src/scene/force_field.rs:165`) then finds `force_field_access` empty. `admits_group` (`force_field.rs:~30`) returns false, and the password set is empty too.

**Wrong outcome.** Every ship that is not admitted by a password is stopped at the boundary. That includes the tower owner's own corporation, as long as `SetTowerPassword` has not been called again since the restart. A pilot who entered a password before the restart is also locked out until they type it again, and `SetShipPassword` is client-driven.

**Proposed failing test**
- Host, `ControlTowerHostTests`: build an online tower with `allowCorp = true`, swap in a fresh `FakeSim`, call `RestoreDeployedAsync(system)` and wait. Assert that a `SimSetForceFieldAccess` with the tower's corporation is in `Heard`. It is absent today.
- Host, `SetShipPasswordHostTests`: same restart, then assert that the pilot's `SimShipForceFieldPassword(matched: true)` is re-sent. It is absent today.

**Proposed minimal fix.** After the control tower branch places the tower, send `SetForceFieldAccess` built from the tower's record and saved JSON (the same values `SetTowerPasswordAsync` sends), then call `RematchShipPasswordsAsync(system)`. Do the send inside the restore path, not on a timer.

---

## F2 (High, confirmed): a gun reload clears a pilot's password credential, and the simulation keeps the admission

**Outcome (fixed):** `ControlTowerHostTests.A_gun_reload_keeps_the_pilot_ship_password_so_a_later_tower_password_change_refuses_it` failed first (expected `Matched = False` after the tower password change, got `True`). The reload no longer removes the credential: the `TryRemove` line is deleted from `SpaceSessions.AdjustAsync`. Verified: `ControlTowerHostTests` 24 of 24, `AmmoHostTests` 16 of 16, `SetShipPasswordHostTests` 1 of 1 passed. The review's test location (`SetShipPasswordHostTests`) was replaced by `ControlTowerHostTests`, which already has the tower and pilot helpers.

**Files**
- Clear: `dotnet/src/Eve.Services.Space/SpaceSessions.cs:254-257` (`AdjustAsync`: `_shipStarbaseCredentials.TryRemove(id, out _)`).
- Reload callers: `dotnet/src/Eve.Services.Dogma/DogmaIMService.Reloads.cs:181` (`AnswerEmptyAsync`, `rounds.AdjustAsync(... new RoundsChange.Add(0, 0) ...)`), `:107`, `:206`, `:253`. `OnGunRanDry` (the `GunRanDry` event) reaches `ReloadEmptiedGunAsync`, which reaches these.
- Rematch skips the pilot once its credential is gone: `SpaceSessions.ShipPasswords.cs:35-40`.
- Simulation keeps the admission: `crates/eve-space/src/scene/force_field.rs:267-308` (`set_force_field_password` is keyed by session, and no removal is sent).
- EVE.js does not do this. `starbaseAccessRuntime.js:234-244` (`matchesPassword`) checks only the credential's ship and character, and `:265-294` (`setShipPassword`) stores the credential on the session. A reload clears nothing there.

**Caller path.** Pilot P sets password `A` on tower T (`SetShipPasswordAsync`, `SpaceSessions.cs:181-205`). P is admitted, with harmonic T. P's gun runs dry in a fight, so `GunRanDry` fires and `AnswerEmptyAsync` calls `AdjustAsync`. That removes P's credential. The owner then changes T's password to `B` (`SetTowerPasswordAsync`, `SpaceContainers.PosMgr.cs:183`). `RematchShipPasswordsAsync` iterates `_shipStarbaseCredentials`, which no longer has P, so nothing is sent for P.

**Wrong outcome.** P, who knows only the old password `A`, stays admitted to the field, and the simulation still holds `(P, T) = true`. A password change does not evict a hostile pilot who entered the old password and then reloaded.

**Proposed failing test**
- Host, `SetShipPasswordHostTests`: `SetShipPassword("A")` for P. Then call `rig.Space.AdjustAsync(...)` with a no-op `afterwards` (the same call the gun-reload path makes). Then `SetTowerPassword(T, "B")`. Assert that the last `SimShipForceFieldPassword` for `(P, T)` is `matched: false`. It is `true` today.

**Proposed minimal fix.** Delete `_shipStarbaseCredentials.TryRemove(id, out _)` at `SpaceSessions.cs:257`. A reload has nothing to do with the password. If a credential must be dropped in another place, send `matched: false` for each tower of the system first.

---

## F3 (Medium, confirmed): the reconcile's send happens after the commit, outside the lock

**Outcome (fixed):** `ControlTowerHostTests.A_placement_in_flight_is_not_overtaken_by_an_offline_sync_that_takes_the_field_out` failed first (the simulation's last command was the stale `SimPlaceForceField`). `ReconcileForceFieldAsync` now takes a per-tower gate (`_forceFieldGates`) around the whole reconcile, so the item commit and the send to the simulation happen in one order. Verified: `ControlTowerHostTests` 25 of 25 passed.

**Outcome (remainder, fixed):** an offline tower with no field item is still sent a `RemoveForceField` at each reconcile. A refused or unreachable removal is asked again, up to five attempts 200 ms apart, until the simulation answers empty (taken out) or `FORCE_FIELD_NOT_IN_SPACE`. The retry is bounded: after the fifth refusal the sync fails as before, and the next sync asks again. Two tests written first, both failed before the fix (`An_offline_tower_without_a_field_item_is_still_told_to_take_its_field_out`: the sync sent nothing; `An_offline_tower_whose_removal_is_refused_is_asked_again_until_the_simulation_says_the_field_is_gone`: empty list, expected three asks). Verified after the fix: `ControlTowerHostTests` 30 of 30 passed, including the exact-count assertions of the offline and unanchor tests. `FakeSim` gained a test-only queue of removal answers (`ForceFieldRemovalAnswers`). `FakeSim` gained a test-only placement hold (`ForceFieldPlacementRelease`, `ForceFieldPlacementEntered`); no protocol change.

**Files**
- `dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:519-553` (`ReconcileForceFieldAsync`). The transaction holds `LockAndGetAsync` (`PgDeployedStore.cs:48`, `FOR UPDATE`) and commits at about line 540. The sends are at 544 (`PlaceForceFieldAsync`) and 550 (`RemoveForceFieldAsync`), after the lock is released.
- Removal is sent only when a DB item exists: line 535, `else if (await RemoveForceFieldInTransactionAsync(...) is { } removed)`. A tower with no item therefore sends nothing when offline.
- Callers that run reconcile concurrently: `OfflineControlTowerAsync` (`:746-772`, sync at 761 and 772), the online job `SpaceComposition.cs:553-567`, and `RestoreDeployedAsync` (`SpaceContainers.Deployed.cs:127`).

**Caller path.** Sync A (tower online) commits and has an item. Sync B (tower offline, from an offline RPC) takes the lock after A, finds the item, commits its deletion, and sends `RemoveForceField`. A then sends `PlaceForceField`, which reaches the simulation after the removal. The simulation ends with a field, and the database has no item.

**Wrong outcome.** A ghost field stays in space. Every later sync of the offline tower finds no item, so `removal` stays null and nothing is sent. Only a simulation restart clears the ghost.

**Proposed failing test.** Host, `ControlTowerHostTests`: a `FakeSim` whose `PlaceForceFieldAsync` waits on a `TaskCompletionSource`. Start an online sync, let it commit and hold, then run an offline sync to completion, then release the placement. Assert that the simulation's last command for the tower is a removal. It is a placement today. Also assert that a third sync of the offline tower sends a removal even with no item. It sends none today.

**Proposed minimal fix.** Hold a per-tower `SemaphoreSlim` (the pattern of `_combatLogoffGates` in `SpaceSessions.cs`) from before the transaction through the send. Separately, for an offline tower, always send `RemoveForceFieldAsync`. The simulation answers `FORCE_FIELD_NOT_IN_SPACE`, which the code already tolerates (line 551).

---

## F4 (Medium, confirmed): a stale password match can finish after the rematch and overwrite it

**Outcome (fixed):** `ControlTowerHostTests.A_tower_password_change_is_the_last_word_on_a_pilot_whose_ship_password_match_was_still_running` failed first (the last `SimShipForceFieldPassword` was the stale `Matched = True`). A pilot's match and its sends (`SetShipPasswordAsync`) and the system's rematch (`RematchShipPasswordsAsync`) now run under one per-system gate (`_passwordGates`), so the rematch that follows a saved tower password is always the last word. Verified: `ControlTowerHostTests` 26 of 26, `SetShipPasswordHostTests` 1 of 1 passed. The test drives the race through a test-only seam `SpaceSessions.AfterShipPasswordMatch` (null in the game, awaited after the match is computed and before the send).

**Files**
- `dotnet/src/Eve.Services.Space/SpaceSessions.cs:197-205` (`SetShipPasswordAsync`). The credential is stored at 197. The match is computed at 199 from a tower configuration read before the await. The send is at 204-205, after a check (201-203) that compares only the credential and the ship.
- `dotnet/src/Eve.Services.Space/SpaceSessions.ShipPasswords.cs:35-47` (`RematchShipPasswordsAsync`), which runs from `SetTowerPasswordAsync` (`SpaceContainers.PosMgr.cs:183`).

**Caller path.** P sets `A` while T's password is `A`. The match takes time (scrypt, one hash per tower). During it the owner changes T's password to `B`. The rematch sees P's new credential and sends `(P, T) = false`. P's match, computed against the old hash, then finishes with `true`. The check at 201-203 passes, because the credential is still current, so the stale `true` is sent last.

**Wrong outcome.** The simulation holds `(P, T) = true`, so P is admitted with the old password `A`, which no longer works.

**Proposed failing test.** Host: give `DeployedRecords` a configuration read that blocks on a `TaskCompletionSource`. Start `SetShipPassword("A")`, change T to `B` through `SetTowerPassword` to completion, then release the gate. Assert that the last `SimShipForceFieldPassword` for `(P, T)` is `false`. It is `true` today.

**Proposed minimal fix.** Serialize `SetShipPasswordAsync` and `RematchShipPasswordsAsync` for a system with one `SemaphoreSlim`. Alternatively, after the await, re-read the tower's configuration and send only if the digest answer still matches the stored hash.

---

## F5 (Medium, confirmed): a pilot's corporation or alliance changes while inside the field are never seen

**Outcome (fixed, narrower than the review said):** the review's "no update path" claim is wrong for the Host. A corporation or alliance change already reaches the simulation: `relation.affiliation.*.changed` (`AllianceRegistryService.NotifyAffiliationChangedAsync`) calls `SpaceSessions.AffiliationChangedAsync`, which sends `ShipChanged` (protocol 8, `SHIP_SPEC`), and the existing test `A_pilot_whose_corporation_changed_has_its_ship_given_to_the_simulation_again_with_the_new_affiliation` covers it. The real gap was in the simulation: `respec` replaced the spec but did not refresh the harmonic of a pilot already inside a field, so the admission it had stayed until some other refresh. Fixed in `scene/session.rs` (`respec` refreshes the field harmonic when the corporation or alliance changed). Red first: `a_pilot_who_leaves_the_admitting_corporation_inside_the_field_loses_its_harmonic_when_its_ship_is_given_again` (harmonic stayed `1050000099`, expected `-1`). Verified: `cargo test -p eve-space --lib` 1094 of 1094 passed. No protocol message was added (`SetPilotAffiliation` was not needed and the protocol is unchanged). Not done: the admission check at a boundary crossing already reads the spec, so it needs no change.

**Files**
- Snapshot: `crates/eve-space/src/scene/force_field.rs:165-205` (`enforce_force_fields` and `refresh_force_field_harmonic` both read `ship.spec.corporation_id` and `ship.spec.alliance_id`).
- No update path: a search of `crates/eve-space/src/**/*.rs` finds writes to `spec.corporation_id` only in tests (`scene/force_field_tests.rs:73-74`, `drones/tests.rs`, `deployable/tests.rs`). `SimMessages.cs` has no message that carries a pilot's affiliation.
- EVE.js checks live authority at every crossing: `starbaseAccessRuntime.js:246-263` (`canShipEnterForceField` reads `getAuthority(session)` and `getAllianceMemberAuthority`).

**Caller path.** Pilot P (corporation A) is admitted inside a field of a tower owned by corporation A. P leaves corporation A while still in space. The Host sends nothing to the simulation, so the spec still says A and P stays admitted. The reverse also holds: a pilot who joins A while inside is not admitted until a move re-enters the field. `refresh_force_field_harmonic` is never called for it.

**Wrong outcome.** Former members stay admitted. New members are denied until they re-cross the boundary.

**Proposed failing test.** Rust, `scene/force_field_tests.rs`: admit a pilot by corporation, then send a pilot affiliation update that sets corporation 0, move the ship inward, and expect a denial. The test cannot compile today because the message does not exist. That absence is the failure. A Host test should show the update is sent when the character's corporation changes.

**Proposed minimal fix.** Add a protocol 103 message `SetPilotAffiliation(session, corporation_id, alliance_id)`. The Host sends it on corporation and alliance changes and on entry. The simulation updates `ship.spec` and calls `refresh_force_field_harmonic`.

---

## F6 (Medium, confirmed): a refused placement leaves the item and aborts restore for the whole system

**Outcome (fixed):** two red tests first. (1) `A_refused_field_in_a_restore_does_not_stop_the_other_records_of_the_system_being_put_back`: without the restore guard the cynosural field of the same system was never put back (`Assert.Single` on an empty `SimDeployed`); confirmed by removing the guard temporarily, then restoring it. (2) The pinned test `A_field_the_simulation_refuses_is_an_error_and_is_placed_again_once_the_simulation_accepts_it` was corrected: it asserted that the refused item stays, which breaks the invariant "an item exists only with a field in the scene"; it now asserts that the item is gone and the next sync writes a new one. Fixes: a refused placement deletes the item it just wrote, in its own transaction, then still throws for the RPC caller (`ReconcileForceFieldUnderGateAsync`); `RestoreDeployedAsync` logs a refused field for its tower and carries on with the other records. Verified: `ControlTowerHostTests` 27 of 27 passed. The review's suggestion to keep the item on refusal was rejected: the field is not in the scene, so the item must go.

**Files**
- `dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:519-553`. The item is committed (about line 540) before the send at 544. A refusal throws at 546, after the commit.
- `dotnet/src/Eve.Services.Space/SpaceContainers.Deployed.cs:84-172`. The `foreach` runs inside one `try`. The reconcile call at 127 is not wrapped, so its exception lands in the catch at 170-172 and every later record in the system is skipped.
- `dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:759-772`: the offline path throws after its state commit, so the RPC reports failure for a change that was made.

**Caller path.** A pilot enters system S (`SystemEntered`, `SpaceContainers.cs:692-695`). Tower A is online and its placement is refused with `SYSTEM_UNKNOWN`, which the simulation returns when its universe has no such system. Tower A's item is already committed. The throw at 127 skips the rest of the records in S, including a cynosural field or another tower that comes after A in the list.

**Wrong outcome.** Deployed records in the system are not restored. The item for A stays with no field in space, which breaks the stated invariant "one field item and one field in its scene" from the doc comment at 515-518.

**Proposed failing test.** Host, `ControlTowerHostTests`: a `FakeSim` with `ForceFieldPlacementReason = "SYSTEM_UNKNOWN"`, a tower and a cynosural field in the same system, then `RestoreDeployedAsync`. Assert that the cynosural field is placed. It is not today. There is no restore-reconcile test in the Host suite at all.

**Proposed minimal fix.** Wrap `ReconcileForceFieldAsync` in its own `try/catch` inside the restore loop, and log. In the sync path, either delete the committed item on refusal in the same transaction or log without throwing. Keep the throw only for the offline RPC's caller, and only after the state change is reported.

---

## F7 (Low-Medium, confirmed): alliance admission reads the record, not live membership

**Outcome (fixed, one gap noted):** `ControlTowerHostTests.A_tower_admits_its_owner_alliance_by_the_live_membership_of_the_owner_corporation_and_takes_it_away_when_the_corporation_leaves` failed first (the sync after the owner corporation left its alliance sent nothing; the record still named the alliance). Fix: `SpaceContainers.AllianceOfCorporationAsync` resolves the owner corporation's live alliance the way EVE.js `getTowerAllianceID` does (the corporation is not deleted, its alliance is not deleted, and the alliance still lists the corporation); the host wires it in `SpaceComposition` from the corporation store. The password save, the restore and the sync all send that live value. A sync re-sends the admission when the live value differs from the one last given to the simulation (`RefreshForceFieldAccessAsync`). Verified: `ControlTowerHostTests` 31 of 31 passed. Gap: no event from the alliance registry reaches the containers, so a membership change is taken up at the tower's next sync (the online control tower job runs it periodically), not at the moment it happens. When the hook is not set (a rig without the corporation store) the record's alliance is used, as before.

**Files**
- `dotnet/src/Eve.Services.Space/SpaceContainers.PosMgr.cs:185-186` sends `updated.AllianceId`, the tower record's alliance.
- EVE.js resolves the alliance live: `starbaseAccessRuntime.js:155-160` (`getTowerAllianceID` requires the owner corporation to be in the alliance and the alliance to be non-deleted) and `:162-171` (`getForceFieldAccess` takes it on every call).
- Nothing sends `SetForceFieldAccess` when the owner corporation leaves or joins an alliance.

**Caller path.** Tower T's owner corporation leaves alliance X. The record's `AllianceId` may still be X until something rewrites it. The simulation keeps admitting X members until the next `SetTowerPassword`.

**Proposed failing test.** Host: change the corporation's alliance, then sync, and assert that a `SimSetForceFieldAccess` with alliance 0 is sent. It is not sent today.

**Proposed minimal fix.** Resolve the alliance through the corporation-state lookup at every `SetForceFieldAccess`, and re-send it on alliance membership changes.

---

## F8 (Low-Medium, partially confirmed): unanchor completion can leave a field and an orphan item

**Outcome (fixed, the item half rejected):** the review's proposed check, that no `ForceField` item remains after the unanchor, passes today and does not show the defect. Traced: the offline reconcile deletes the item in the same transaction that commits the removal, so a failed send after the commit leaves no item. What survives is the field in the simulation, because `CompleteControlTowerUnanchorAsync` only called `RemoveDeployed` (the hull). `ControlTowerHostTests.A_tower_whose_offline_sync_could_not_take_its_field_out_of_the_simulation_has_it_taken_out_when_its_unanchor_completes` failed first (no `SimRemoveForceField` after completion). The completion now takes the tower's field out of the database and the simulation before it removes the hull (`TakeOutForceFieldAsync`, under the tower's gate). A refusal or an unavailable simulation is logged, as the hull removal is, so the unanchor still completes. Verified: `ControlTowerHostTests` 28 of 28 and the `Unanchor` filter 11 of 11 passed.

**Files**
- `dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:1048-1058`: `CompleteControlTowerUnanchorAsync` calls `_sim.RemoveDeployed(towerId)` (line 1056), which removes only the hull. It never calls `RemoveForceField`, and it deletes the record.
- Unanchor requires an offline tower (`UnanchorControlTowerAsync`, about 1004-1030, `state != TowerOffline` refuses). The offline sync removes the field first, so the normal path is clean. That is why this is only partially confirmed.
- `:603-606` (`SynchronizeControlTowerSlimStateAsync`) returns early when the record is gone, so no later sync can clean up.

**Caller path.** The offline sync's send fails after its commit (see F3 and F6). The owner then unanchors and the job completes. The record is deleted. The field is still in space, and the item is still in the database with no record to reconcile it.

**Proposed failing test.** Host: fail the `RemoveForceFieldAsync` once, offline, then run the unanchor job to completion. Assert that no `ForceField` item remains in the system. It remains today.

**Proposed minimal fix.** In `CompleteControlTowerUnanchorAsync`, also delete the field item and send `RemoveForceField` before the record is deleted.

---

## F9 (Low, confirmed): the cancel rule diverges from EVE.js

**Outcome (Follow and Orbit fixed; Goto rejected):** the EVE.js side was read before any change (`starbaseForceFieldBoundary.js:160-164` tests `ship.targetPoint`, and `subwarpCommands.js`, `evolve.js:362-370`, `evolve.js:392-413` and `evolve.js:878-879` set that point per order).
- **Goto: rejected, no change.** EVE.js sets a GotoDirection's target point to position plus direction times 1e16 (`subwarpCommands.js:269-272`). Its test `dot(target - position, normal) <= 0` is therefore the sign of `dot(direction, normal)`, which is the Rust `Course::Goto` arm. A GotoPoint's point is the commanded point, which the Rust `GotoPoint` arm already tests. The finding's claim that Goto tests the heading where EVE.js tests the target does not hold for these two orders.
- **Follow: fixed.** EVE.js's point is the standoff on the ring of radius `range + ship radius + target radius` about the target, on the line from the target through the ship (`evolve.js:362-370`). The Rust boundary now uses that point, taken where the ship started its move, and cancels only when `dot(point - position, normal) <= 0`. It was always cancelled before.
- **Orbit: fixed, with one approximation.** EVE.js's point is the position plus the orbit acceleration times `ORBIT_GOTO_SCALE` (`evolve.js:878-879`), so its sign is the sign of the acceleration along the normal. The Rust counterpart is the steering vector of `orbit_step`, extracted unchanged as `orbit_wanted` and read through `orbit_direction`. The Rust orbit model is not EVE.js's `orbitAcceleration` (which uses an orbital axis and a radial and transverse term), so the two can disagree where their directions are close to the normal.
- **Still differs, not changed:** a Follow or Orbit with no resolvable target is still cancelled (EVE.js stops the ball, which has the same effect); a target that is a station or other body is not resolved, so it is still cancelled; Warp still always cancels, which the finding did not list and which was not verified against EVE.js.
- **Tests:** four new tests in `scene/force_field_tests.rs`. Two were written as the outward cases and failed before the fix (`a_follow_whose_target_lies_outward_from_the_field_is_not_cancelled`: `Stop`, expected `Follow`; `an_orbit_that_pulls_the_ship_out_of_the_field_is_not_cancelled`: `Stop`, expected `Orbit`). Two inward cases pass before and after. Verified after the fix: `cargo test -p eve-space --lib force_field` 42 of 42 passed; `cargo test -p eve-space --lib` 1098 of 1098 passed.

**Files**
- Rust: `crates/eve-space/src/scene/force_field.rs:247-252`. `Goto` tests the ship's heading, `Follow` and `Orbit` always cancel, and `Warp` always cancels.
- EVE.js: `starbaseForceFieldBoundary.js:160-164`. The test is `mode !== "STOP" && (!target || dot(target - position, normal) <= 0)`, so it uses the target point. A `Goto` is cancelled only when its target pulls back in.

**Caller path.** A `Goto` pilot whose target lies outward from the boundary, but whose heading points inward, is cancelled by Rust and not by EVE.js. Outward orders that the client can still use are lost.

**Proposed failing test.** Rust: `Course::Goto` with an outward target and an inward `direction`. Move the ship in. Expect `cancelled == false`. It is `true` today.

**Proposed minimal fix.** Use the target point for `Goto`, and the entity or orbit position for `Follow` and `Orbit`, with the same dot test. Cancel only when the target pulls back in.

---

## F10 (Low, confirmed): test gaps

**Outcome (fixed, test gap closed):** two tests added, both checked by breaking the code they cover. (1) `a_move_the_scene_settles_is_stopped_at_the_field_and_its_order_is_cancelled` (`scene/force_field_tests.rs`) drives `Scene::settle_pilot`, the production settle path in `scene/movement.rs`, and asserts the boundary stop, the `Stop` cancel and the `SetBallPosition` correction are all told to the clients. With the `publish_force_field` call of `settle_pilot_after` removed it failed (`the cancel is told to the clients: []`); restored, it passes. (2) `A_restore_puts_back_the_field_of_an_online_tower_whose_item_is_gone_and_writes_its_item_again` (`ControlTowerHostTests.cs`) drives `RestoreDeployedAsync` after the tower's item is lost. With the restore's reconcile call removed it failed (`The collection was empty`); restored, it passes. Its first version asserted that the restored field keeps the old id. That expectation was wrong: the item was gone, so the restore allocates a new id. It was corrected to assert that the placed field is the item's id and is placed for the same tower and system. No assertion was weakened.

- `crates/eve-space/src/scene/force_field_tests.rs:41-54` (`move_and_tell`) calls `enforce_force_fields` and `publish_force_field` itself. It does not call the production settle path (`crates/eve-space/src/scene/movement.rs:50-60`, which captures `harmonic_before` and `before`). A wiring bug there would pass every test.
- No Host test covers `RestoreDeployedAsync` reconciliation (search of `dotnet/tests/Eve.Services.Space.Tests`).
- `dotnet/tests/Eve.Services.Space.Tests/ControlTowerHostTests.cs:1866-1884` asserts that a refused placement throws and that the item stays. This matches the doc comment but enshrines the F6 invariant break. It should be changed with the F6 fix.

**Proposed failing tests.** The settle-path test should drive `settle_one` and assert the same correction as `move_and_tell`. The restore test is given under F6.

---

## F11 (Low, confirmed): non-positive ids are accepted

- `crates/eve-space/src/link/decode_message.rs:2759-2781` and `dotnet/src/Eve.Space/SimCodec.cs:1336-1349` accept `field_id <= 0` and `tower_id <= 0`.
- The harmonic is the tower id (`deployable.rs:211-213`, `force_field.rs:297`). A tower id of 0 gives harmonic 0, which the Rust encoder emits and the C# decoder refuses (`SimCodec.cs:1664-1670`), so the frame fails.
- C# callers use `ReadPositiveId` and a positive `DeployableId`, so this is not reachable from the current Host. It is still a validation gap.

**Proposed failing test.** Codec: a `PlaceForceField` frame with `tower_id = 0` must decode as an error. It decodes today.

**Proposed minimal fix.** Refuse `field_id <= 0` and `tower_id <= 0` in both decoders.

---

## Rejected candidates (checked, not defects)

- **"A tower password cannot be cleared."** EVE.js also keeps the existing hash on `password === false` (`starbaseAccessRuntime.js:110-136`: `credentials` stays null and the config keeps its salt and hash). The Host matches. This is a parity observation, not a defect.
- **Boundary exactly on the radius.** `denied_entry` (`force_field.rs:100-125`) uses strict `<` for starts-inside and ends-inside, as `resolveDeniedEntry` does (`starbaseForceFieldBoundary.js:50-84`). A ship on the surface moving inward is stopped at its own point, so there is no teleport. A tangential ship on the surface gets `inward == 0`, which returns None. The repulsion constants and the boundary-contact speed match EVE.js (`:145-159`).
- **Harmonic -1 or 0.** Decode and encode both refuse `0` and `< -1` (`decode_message.rs:2749-2756`, `SimCodec.cs:1664-1671`, `SimCodec.Encode.cs:1737-1744`). Rust emits only `-1` or a tower id. The only live gap is F11.
- **Codec order and sizes.** Checked by reading both sides: place (i64, i64, i32, i32, i32, text, vec3, f64), access (i64, i32, i32, i32, u8, u8), password (u64, i64, i32, u8), harmonic (u64, i64, i64), placed and removed (i64, text), remove (i64, i32). `payload.rs:1029-1070` and `:2042-2046`, `decode_message.rs:2749-2823`, and `SimCodec.cs:1336-1379`, `:1664-1671`, `SimCodec.Encode.cs:1525-1546`, `:1737-1744` all agree. Kinds 180-186 are unique in `kind.rs:37-50` and `SimCodec.Shared.cs:39-52`.
- **Migration 0034.** The constraint chain matches 0026, 0027, 0031 and 0034. The partial unique index on `launcher_id` where `kind = 6` holds at most one item per tower.
- **Two fields for one tower in the database.** Prevented by `FOR UPDATE` (`PgDeployedStore.cs:48`) plus the unique index. The simulation also removes stale same-tower fields before inserting (`deployable.rs:1028-1085`). The only remaining race is the send order (F3).
- **Rematch matching the wrong pilot.** `RematchShipPasswordsAsync` checks character id, ship id and session, then reference-equality of the credential (`SpaceSessions.ShipPasswords.cs:39-44`). EVE.js checks the same identity (`starbaseAccessRuntime.js:234-236`).
- **Password or digest leaked by logs.** No log call in the reviewed files includes a password or digest. The in-memory digest matches EVE.js (`starbaseAccessRuntime.js:274`, `credential.digest`). It is password-equivalent in memory, as in EVE.js.
- **scrypt implementation.** Checked by reading against RFC 7914: Salsa20/8 rotations and order, BlockMix output order, Integerify on the last 64-byte block, and the final PBKDF2. Not run.

## Parity notes (not defects)

- An empty password: EVE.js returns `STARBASE_PASSWORD_REQUIRED`, the Host returns `SettingsInvalid`. Error text differs only.
- EVE.js `setShipPassword` checks only online towers and stops at the first matching one. The Host sends every tower's answer, and the simulation decides. The end result is the same.

## Limits

- Nothing was built or run. Every finding is from reading.
- The EVE.js client files under `/home/user/eve-logic` could not be read (directory denied). The harmonic semantics in `playerOwned.py:91-115` were not checked.
- `EVE.js` files were read with the Read tool, as the brief asked. The `git grep` route was blocked by the shell hook.
