# POS fuel, force field, and industry arrays (2026-11-17)

> **Current state (2026-11-19).** `PROTOCOL_VERSION` is 106 (`crates/eve-space/src/link/mod.rs`). The SimLink kinds are in `crates/eve-space/src/link/kind.rs`: 168 to 171 (protocol 102), 180 to 185 (104), 186 (105), 190 and 191 (104), 192 to 194 (106). `oracle/fixtures/simlink/messages.json` holds all of them (checked by name). The sections below are dated slice logs. Where one says that messages.json was not regenerated, that the fixtures were refused by the command policy, or that the file says version 102 or 104, that was the state at that slice and is kept as history; the current state is this block and the slice 5 and 6 sections. A tower's password change re-matches pilots holding a credential (slice 5).

## Scope and source trace

The V24.01 checkout at `/home/user/eve-logic` was searched across the client before editing. POS fuel/module state is read through the tower UI in `eve/client/script/ui/inflight/moonmining.py`; module onlining uses `eve/client/script/ui/services/menusvc.py:2493-2512` and `eve/client/script/parklife/playerOwned.py:328-332`. Force-field admission uses `eve/client/script/parklife/playerOwned.py:91-115,173-191`, and the settings are saved from `eve/client/script/ui/inflight/moonmining.py:1195-1219`. OBO targeting is initiated by `eve/client/script/parklife/playerOwnedTargetMgr.py:84-102`. The industry client calls facility discovery/detail/locations from `eve/client/script/industry/facilitySvc.py:42-72,114-120` and job installation/status from `eve/client/script/industry/industrySvc.py:92-110,139-160`.

The broad `rg` checks across `/home/user/eve-logic/eve` found the `forceFieldID` comparison only in `playerOwned.py:106-109`, the OBO calls only in `playerOwnedTargetMgr.py:87,102`, and the bunker lookup only in `factionWarfareSystemInfoContainer.py:54`. Industry tax reads/writes are consumed by `facilitySvc.py:90-94` and `facilityTaxWindow.py:30,73`. The requested `docs/project/coverage-sweep-1.md` is not present in this checkout; the five POS partial rows were cross-checked in `docs/project/service-audit/EVE_RS_COVERAGE.csv` and the supplied POS audit documents.

The comparison source is the pinned EveJS tree at `/home/user/evejs-261108.1`. Relevant behavior is in `server/src/services/ship/controlTowerRuntime.js:935-1032,1488-1554` (field create/reconcile and online transitions), `server/src/services/starbase/starbaseFuelRuntime.js:410-507` (fuel-failure shutdown), `server/src/services/starbase/starbaseAccessRuntime.js:162-170,216-263` (harmonic and admission), `server/src/services/starbase/starbaseControlRuntime.js:210-225` (OBO delegation), and `server/src/services/industry/starbaseIndustryRuntime.js:19-104` (POS industry profiles and association). EveJS is used for observable behavior; its state ownership and implementation are not copied.

## Verified changes

### Fuel failure and module state

An unpaid tower fuel period now writes the tower offline, writes associated online/onlining modules offline in the same PostgreSQL transaction, and cancels pending module-online jobs. After the transaction commits, the Host reads persisted tower/module state and updates the live simulator slim items (`posState` and `posTimestamp`). The same module shutdown is applied when a tower is explicitly taken offline. The change is in [`SpaceContainers.ControlTowers.cs`](../../dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:98).

The database-backed regression in [`ControlTowerHostTests.cs`](../../dotnet/tests/Eve.Services.Space.Tests/ControlTowerHostTests.cs:916) onboards two modules, onlines them, triggers an empty-fuel period, and checks both persisted state and simulator slim state after the scheduler transaction. This verifies Host/database/simulator state; it does not establish graphical-client rendering or the complete fuel-bay UI.

### POS assembly-array industry

The Host now resolves anchored, online POS assembly arrays and laboratories associated with a live control tower. The supported installation/activity/filter data is checked in at [`starbase-assembly-profiles.json`](../../dotnet/src/Eve.Host/Data/starbase-assembly-profiles.json) for V24 client build 3396210; the resource hashes and selection basis are recorded in the JSON. The Host registers deployed modules in facility discovery and implements detail, owner-bound facility taxes, corporation-hangar locations, and job install/read behavior. Jobs preserve the tower association internally, disappear from the public payload where EveJS omits it, and report paused while the tower/facility is offline. Material/output custody remains the existing corporation inventory authority.

The registered Host/PostgreSQL regressions are in [`IndustryFacilityHostFlowTests.cs`](../../dotnet/tests/Eve.Host.Tests/IndustryFacilityHostFlowTests.cs:72): facility listing/detail/locations and exact public fields; at line 137 tax authorization, notification and persistence across Host restart; and at line 206 a real corporation-hangar job install, material consumption, persisted tower association, offline pause and online completion. Tax changes require Station Manager access and notify `OnFacilitiesUpdated` after commit. EveJS `industryPayloads.js:113-184` omits internal `inventoryLocationID` and `starbaseTowerID`; the tests assert those fields do not leak.

Only the profiles in the checked-in data file are supported. No graphical V24 industry flow was run.

Capital profiles receive the V24 type-dogma security check before the Host reports them online. Profiles whose dogma requires a sovereignty level or infrastructure-hub upgrade remain listed offline: Host does not yet calculate authoritative hub-upgrade power/fuel state (the same unresolved budget recorded in `TODO.md` ST-17 c). `Capital_POS_assembly_array_obeys_its_V24_system_security_limit` covers the registered Host response for type 24656 in Jita. This is intentionally fail-closed; it is not evidence that the capital arrays can complete jobs in a qualifying system.

### POS battery control grant (2026-11-18)

V24.01 `playerOwned.AssumeStructureControl` (`playerOwned.py:428-453`) calls `posMgr.AssumeStructureControl(itemID)` for a battery, and `RelinquishStructureControl` (`playerOwned.py:455-475`) releases it. Both take one item ID and return nothing the client reads.

Implemented in the Host:

- `AssumeStructureControl`: the battery must be a starbase module whose type carries dogma attribute 1167, must be online or onlining, and the calling pilot must belong to its corporation, be in its system and in space, and be within interaction distance (`RequireControlTowerAccessAsync`). The grant is one row of `starbase_battery_control` written under the battery's deployed-row lock. A grant held by another pilot is refused. Asking again for a grant the pilot holds is idempotent.
- `RelinquishStructureControl`: releases the calling pilot's grant only. It is idempotent, and it does not need the interaction-distance check, so a grant cannot be stranded.
- Leaving: when the holder leaves the cluster, its grants are released through `SpaceSessions.OnCharacterLeavingAsync`.
- No server notification is sent. The client's own `OnAssumeStructureControl` / `OnRelinquishStructureControl` are local scatters.

Verification status:

- `PosMgr_missing_client_methods_are_dispatched_through_the_registered_host_service` passed (1 passed, 0 skipped). It sends both methods through the registered Host and checks the argument shapes.
- `ControlTowerHostTests.PosMgr_battery_control_is_held_by_one_pilot_over_host_wire_and_is_released_by_relinquish_or_logoff` covers exclusivity, idempotence, relinquish, the holder's leaving, and the offline and non-battery refusals. Run on PostgreSQL `eve_p4` on 2026-11-18 after the SDE group join fix: 1 passed, 0 failed, 0 skipped. The class `ControlTowerHostTests` ran 11 passed, 0 failed, 0 skipped; PosMgr filters ran 6 passed (Space.Tests) and 5 passed (Host.Tests), all 0 skipped.
- The battery type is looked up at test time from the SDE: `sde.types` joined to `sde.groups` on the group key, filtered to `categoryID` 23 and published, then attribute 1167 from `sde.typeDogma`. The pinned SDE has 211 published category-23 types; 16631 and 16688 carry attribute 1167.

Still open: `AddTargetOBO` and `RemoveTargetOBO`, the `OnTargetOBO` notification, and graphical acceptance. See the OBO blocker below.

### POS OBO targeting: blocked, client shapes verified (2026-11-18)

`AddTargetOBO` and `RemoveTargetOBO` are not implemented in this run, and no RPC name is registered, because a registered handler would be a fake success.

Verified client contract (V24.01, read through codebase-memory `get_code_snippet` after the Read tool was refused for `/home/user/eve-logic`):

- `PlayerOwnedTargetMgr.LockTargetOBO(sid, tid, who)` (`eve/client/script/parklife/playerOwnedTargetMgr.py:84-99`) calls `godma.GetDogmaLM().AddTargetOBO(sid, tid)` and reads `(flag, targetList)`; a false flag runs `OnTargetAdded`. A `UserError` named `DeniedShipChanged` is swallowed; other messages are shown.
- `PlayerOwnedTargetMgr.UnLockTargetOBO(sid, tid)` (lines 101-103) calls `GetDogmaLM().RemoveTargetOBO(sid, tid)` and ignores the result.
- `OnTargetOBO(what, sid, tid, reason)` consumers: `playerOwned.py:501-507` (`add` stores `currenttargets[sid] = tid`, `lost` deletes it) and `ui/inflight/moondefencebutton.py:764-781` (`add`, `clear`, `lost`, matched on `sid` = the battery).
- EveJS (`/home/user/evejs-261108.1/server/src/services/starbase/starbaseControlRuntime.js`): `addTargetOBO(session, batteryID, targetID)` (line 210) goes through `getControlledContext`, which requires the grant held by this session, refreshes the grant, and calls `scene.addTarget` for the battery's session. `removeTargetOBO` (line 217) cancels a pending lock and removes the target with `notifySelf` and `notifyTarget`. `notifyStarbaseTargetEvent` (line 66) sends `OnTargetOBO` to the grant holder only, with the id type `charid` and args `[what, batteryID, targetID, reason]`. Other pilots never receive OBO target IDs.

eve.rs state:

- The DogmaIM targeting partial (`dotnet/src/Eve.Services.Dogma/DogmaIMService.Targeting.cs`) has only the ship `AddTarget` / `RemoveTarget` / `CancelAddTarget` / `ClearTargets` / `GetTargets` / `GetTargeters` surface. `OrderAsync` acts on the session's own ship. The DogmaLM bound object does not exist in `dotnet/src` (no `DogmaLM` symbol).
- The Rust `crates/eve-space/src/targeting/mod.rs` holds locks for a session ship or a controlled Upwell structure. `check_lock` (`targeting/lock.rs`) reads `self.pilots[session]`, so a battery lock can reuse it only through a temporary pilot, as `with_controlled_structure_modules` (`scene/session.rs:50`) does for a controlled structure.
- Control towers are deployable balls (`Role::ControlTower`, `deployable.rs`) placed by `Message::PlaceControlTower`. A battery is a module, not a ball, and no link message places it or sets it online.
- Consequence: a handler that accepted `AddTargetOBO(battery, target)` through the existing ship path would change the pilot's ship lock, not the battery's. That is the failure `audit-fixes-dogma.md` already warned about.

Decision (2026-11-18): the battery actor is not committed as dead, unwired code. It needs three slices, in order:

1. Rust: a link message that places a battery and sets its online state (ADR 0007 kind codes on both codecs, `crates/eve-space/src/link/kind.rs` and the C# `SimMessages`), a battery entry in `Scene` with its own `Targeting`, lock through the temporary-pilot path, and translation of the `OnTarget` outputs to `OnTargetOBO` addressed to the grant holder, plus a settle hook for pending locks. Rust unit tests cover lock, pending completion, cancel, remove, the range and cap refusals, and the offline refusal.
2. .NET: a DogmaLM bound object in `Eve.Services.Dogma` with `AddTargetOBO` and `RemoveTargetOBO`. Each validates the `starbase_battery_control` grant held by the caller, that the battery is online, and that the target is valid, then forwards the order to the Rust battery actor. The return shape is `(flag, targetList)`.
3. Host tests through the real registration: grant held and not held, battery offline, invalid target, lock and remove, and the `OnTargetOBO` notification with `[what, batteryID, targetID, reason]` to the holder only.

Graphical V24 acceptance is still required after slice 3; none of these slices has been run against the client.

Slice 1 status (2026-11-18, Rust and codec, protocol 102): done in the simulation and the C# codec. `PlaceBattery` (168), `BatteryPlaced` (169), `BatteryTarget` (170) and `BatteryTargets` (171) are in `crates/eve-space` (`link`, `scene`, `targeting/battery.rs`, `sim/dispatch.rs`) and in `dotnet/src/Eve.Space` (`SimCodec*.cs`, `SimMessages.cs`). The battery has its own lock state; the holder alone is told `OnTargetOBO`. Verified: `cargo test -p eve-space --lib` 1055 passed, 0 failed, 0 skipped (8 battery scene tests, 2 link frame tests and 1 simulation test by `handle`/`poll` among them); `dotnet test dotnet/tests/Eve.Space.Tests` 571 passed, 0 failed, 0 skipped (2 battery codec tests); `cargo clippy -p eve-space --all-targets -- -D warnings` clean; `Eve.Space` Release build with warnings as errors clean.
Not done in slice 1: `oracle/fixtures/simlink/messages.json` was not regenerated at that slice (history). `tools/dev/gen-simlink-fixtures.py` contains kinds 168 to 171, the frame writers, valid and invalid cases, and version 102, but the script could not be run in this session (the command policy refused it), so the shared golden frames do not yet cover the battery kinds. Run the generator before relying on the fixtures; the Rust and C# round-trip tests above are the only coverage until then. The `simlink_fixtures` Rust tests (14 passed) and the C# fixture tests still pass against the old file.

Slices 2 and 3 status (2026-11-18, Host): `AddTargetOBO` and `RemoveTargetOBO` are on the dogma location (`DogmaIMService.Targeting.cs`), through `ISpaceTargeting.BatteryOrderAsync`, `SpaceContainers.Batteries.cs` (grant, online state, SDE numbers, placement, order) and the sim client's battery replies. Verified: `ControlTowerHostTests` battery tests 5 passed, 0 failed, 0 skipped on `eve_p4` (no grant, offline, invalid target, lock and remove, holder-only `OnTargetOBO`); the whole class 16 passed alone. Release builds with warnings as errors: `Eve.Host`, `Eve.Services.Space.Tests`, `Eve.Space`. Open: the full `Eve.Services.Space.Tests` run is not green in one pass (intermittent unrelated failures under parallel runs; the baseline comparison was not completed), the fixture regeneration, a battery going offline outside an order, and graphical acceptance.

### Sentry fire path: slice 1 (2026-11-18, protocol 104)

Client and EveJS facts, read from `/home/user/eve-logic` (codebase-memory `get_code_snippet`) and `/home/user/evejs-261108.1` (Read):

- `DefenceStructureButton.Click` / `ActivateEffect` (`eve/client/script/ui/inflight/moondefencebutton.py:444-459,648-672`) calls `effect.Activate(targetID, 9999999)`. `EffectWrapper.Activate` (`godma.py:976-987`) calls `StateManager.Activate` (`godma.py:2049-2072`), which calls `GetDogmaLM().Activate(itemID, effectName, target, repeat)`. Deactivation uses the same location.
- EveJS `starbaseControlRuntime.js:227-246`: `activateControlledBattery` requires the grant held by this session, the effect name of the battery's offensive effect (`starbaseDefenseRuntime.getBatteryProfile`), and a lock the battery already holds (`TARGET_NOT_LOCKED`). `deactivateControlledBattery` keeps the lock and the grant.
- EveJS `starbaseDefenseRuntime.activateBattery` runs the common generic-module activation (cycle, capacitor, charges, damage). This branch does not run any of those; see the gaps below.

Implemented:

- Rust (`crates/eve-space/src/targeting/battery.rs`, `battery_fire`): a battery has `firing`, a target it locks. A start needs the battery online, a holder, and a locked target (`TARGET_NOT_LOCKED`); a second target while one fires is `MODULE_ALREADY_ACTIVE`; a stop is always accepted. Firing ends with its lock, with a holder change, and when the battery goes offline. Messages `BatteryFire` (190) and `BatteryFired` (191) are in `link/kind.rs`, `link/mod.rs`, `link/payload.rs`, `link/decode_message.rs`, `scene/mod.rs` and `sim/dispatch.rs`. `PROTOCOL_VERSION` is 106. Kinds 180 to 185 (the force field) are in protocol 104 as well.
- C#: `SimCodec` (`ProtocolVersion` 106), `SimMessages` (`SimBatteryFire`, `SimBatteryFired`), `ISimulation.BatteryFireAsync` and `SimClient.BatteryFireAsync`.
- Host: `DogmaIMService.Modules.cs` sends `Activate` and `Deactivate` on a battery's item id to the space before the ship module path (`BatteryFireOrderAsync`, `DogmaIMService.Targeting.cs`). `SpaceContainers.Batteries.cs` `BatteryFireAsync` returns null for an item that is not a player-controlled battery, so ship modules are unchanged. For a battery it refuses a pilot without the grant (`NO_CONTROL_GRANT`, which the client sees as "You must assume control of this starbase battery before operating it."), refuses an effect that is not the type's offensive effect (`EFFECT_INVALID`, checked by `DogmaStatic.OffensiveEffectNameAsync` from SDE `typeDogma` / `dogmaEffects.isOffensive`), refuses an offline battery (`BATTERY_OFFLINE`), and otherwise places the battery with its holder and asks the simulation. Holder and online state are read from PostgreSQL (`starbase_battery_control`, tower state) on every call.

Verification (2026-11-18):

- `cargo test -p eve-space --lib`: 1057 passed, 0 failed, 0 skipped. The 13 battery unit tests include `sim::tests::battery_tests::battery_fire_needs_a_held_target_and_ends_with_its_lock` and `link::battery_tests::battery_fire_refuses_a_flag_that_is_not_a_boolean`. The full `cargo test -p eve-space` (all targets) also finished `ok`; `cargo clippy -p eve-space --all-targets -- -D warnings` and `cargo fmt -p eve-space -- --check` are clean.
- `dotnet test dotnet/tests/Eve.Space.Tests` (Release, warnings as errors): 573 passed, 0 failed, 0 skipped. This includes the new `BatteryCodecTests` round trip and the non-boolean flag refusal.
- `dotnet test dotnet/tests/Eve.Services.Space.Tests --filter ControlTowerHostTests` (Release, warnings as errors, PostgreSQL `eve_p5`): 18 passed, 0 failed, 0 skipped. The two new tests go through the registered `DogmaIMService` on the real grant and battery rows: `Battery_fire_is_held_by_the_grant_holder_and_refused_for_other_pilots` and `Battery_fire_refusals_from_the_simulation_and_offline_batteries_reach_the_holder_as_errors`. The fake simulator answers fire orders; the simulation rules themselves are tested in `eve-space`.
- Release builds with warnings as errors: `Eve.Host.Tests` (which builds `Eve.Host`, `Eve.Services.Dogma`, `Eve.Services.Space`, `Eve.Space`, `Eve.Dogma`, `Eve.Services`), 0 warnings, 0 errors.
- `dotnet test dotnet/tests/Eve.Host.Tests --filter FullyQualifiedName~Dogma` (Release, `eve_p5`): 4 passed, 18 failed. The failure I read is `Npgsql.PostgresException: 42P01 relation "sde_mission.skinrComponentPointValues" does not exist`, raised from `ShipSkinrCatalog.LoadAsync` during `HostComposition.BuildAsync`, so the test host fails before any dogma call. This is the local database, not the battery code. I did not read each of the 18 traces, so I have not shown that all of them have this cause.

Gaps in slice 1 (not verified):

- The simulation records only whether the battery is set to fire, at which target. No damage, cycle, capacitor, charge or ammo is run. EveJS runs them through the generic module activation.
- The holder gets no effect notification (`OnGodmaShipEffect` or module-attribute changes) for a fire, so the client does not show the battery as firing. Graphical acceptance has not been run.
- `oracle/fixtures/simlink/messages.json` was NOT regenerated at that slice (history). `tools/dev/gen-simlink-fixtures.py` now has kinds 190 and 191, their frame writers, valid and invalid cases, and version 104. Running the script was refused by the command policy hook (both the relative and the absolute path), so the shared golden frames do not cover the fire kinds, and the file still says 102. The Rust and C# fixture tests pass against the old file.
- Every `Activate` on a ship module now makes one extra PostgreSQL read (`BatteryFireAsync` → `_deployed.GetAsync`) before the module path. This is not measured.

### Sentry policy: slice 1, scene and wire (2026-11-19, protocol 106)

Verified from the client and EveJS (read this session):

- Client: `MoonMining.InitStandingsPanel` (`moonmining.py:1072-1126`) offers use-alliance-standings, attack if standing is lower than a value (-10 to 10), attack if security status is below a value (-10 to 10), attack if security status is dropping, and attack if at war. Both thresholds are hidden in high security. `SaveStandings` (`moonmining.py:1128-1138`) sends `SetTowerSentrySettings(towerID, standing, status, statusDrop, war, useAllianceStandings)` only when the values changed. `ShowStandings` (`moonmining.py:538-562`) reads `GetTowerSentrySettings` back into the same controls.
- EveJS `server/src/services/starbase/starbaseDefenseController.js`, `tickScene` (lines 115-210) and `evaluateSentryTargetPolicy` (lines 59-76): a battery of an online, anchored tower is driven when it is not manually controlled. A candidate is a visible ship (not cloaked, not warping, not pending dock, `canEntitiesInteractLocally`). The owner corporation and its alliance are never named. War (when `war` is set and a war is active) is checked first and applies in high security. The standing and security-status thresholds apply only outside high security. The standing is the owner's contact standing (the owner corporation's contacts, or the alliance's when `useAllianceStandings` is set), with a pilot entry over the corporation over the alliance and 0 when absent. The status-drop rule needs a persisted security-penalty receipt. Candidates are sorted by distance to the tower, then item id. A battery keeps its previous target while that target stays eligible; otherwise it tries candidates in order with `activateBattery(automatic: true)` (`starbaseDefenseRuntime.js:167-194`), which takes a lock (which may be pending) and then runs the offensive module activation. A lost target or an unavailable battery releases the automatic assignment.
- The EveJS automatic path runs the battery's cycle, capacitor and charge through the generic module activation. eve.rs does not run those for a battery (see the fire section above).

Implemented in eve.rs (slice 1; the Host feed is slice 2):

- `link/kind.rs`: `SENTRY_POLICY` 192, `SENTRY_BATTERY` 193, `SENTRY_PILOT` 194 (no answer). `PROTOCOL_VERSION` 106 in Rust (`link/mod.rs`) and `SimCodec.ProtocolVersion` 106 in C#.
- `targeting/sentry.rs`: the policy (standing, security threshold, war, high-security gate, owner corporation and alliance, online), the pilot facts (corporation, alliance, optional security status, standing, war) and `sentry_pass`, run from `settle_batteries`. A sentry battery has no holder, so it is driven only while no pilot holds it; a holder's locks are never touched. An offline policy, an unbound or offline battery, or a pilot the policy no longer names releases the sentry's lock and fire silently. The sentry locks through `battery_lock` and fires through `apply_fire`, the same rule the holder's fire order uses.
- `sim/dispatch.rs` handles the three kinds in the world group. A policy or pilot message creates the scene like the force field messages do; a binding does not (the battery must already be placed).
- The `Battery.firing` field is now cleared when a battery goes offline (`go_offline`). Before this slice it could survive an offline spell.

Not in this slice (open):

- The status-drop receipt: EveJS reads a persisted security-penalty receipt, and the Host does not feed one. The stored `statusDrop` flag is stored and round-trips, but it is not on the wire and drives no target.
- NPC ships: EveJS can consider any ship entity; eve.rs names only ships that have pilot facts, so NPCs are not chosen.
- The Host feed is done in slice 2 (below).
- Battery damage, cycle, capacitor and charge: not run (as in the fire section).
- Distance: the scene sorts candidates by distance to the battery, not to the tower (EveJS sorts by the tower's distance).
- Freshness: the scene acts on the pilot facts the Host last sent (slice 2 gives the snapshot and its triggers).
- `oracle/fixtures/simlink/messages.json` was not regenerated at that slice (history) (the orchestrator does that). The generator cases for kinds 192 to 194 are added and not run. The harness mapping in `crates/eve-space/tests/simlink_fixtures.rs` and `SimCodecTests.cs` covers the new names. `SimCodecTests.The_fixtures_cover_every_message_kind` already fails on the committed file (its expected list names `BatteryFire` and `BatteryFired`, and the committed file is version 103 without them); it stays failing until the regenerated file lands and its expected list is updated for 192 to 194.

Verification (slice 1):

- `cargo test -p eve-space --lib -- --test-threads=2`: 1099 passed, 0 failed, 0 skipped. Sentry tests: `targeting::sentry_tests` (7) and `link::sentry_tests` (2) pass.
- `cargo test -p eve-space --test simlink_fixtures`: 14 passed, 0 failed.
- `cargo clippy -p eve-space --all-targets -- -D warnings` and `cargo fmt -p eve-space -- --check`: clean.
- `dotnet test dotnet/tests/Eve.Space.Tests -c Release`: 601 passed, 1 failed (the pre-existing `The_fixtures_cover_every_message_kind` above), 0 skipped. The four `SentryCodecTests` pass.
- `dotnet build dotnet/tests/Eve.Services.Space.Tests -c Release -warnaserror`: 0 warnings, 0 errors (its tests are run with the Host slice).

### Sentry policy: slice 2, Host feed (2026-11-19, protocol 106)

What the Host now does (`dotnet/src/Eve.Services.Space/SpaceContainers.Sentry.cs`, `ReconcileSentryAsync`):

- Reads the tower's stored sentry settings (the same `PosMgrTowerConfiguration.Sentry` that `GetTowerSentrySettings` returns) and sends `SENTRY_POLICY`. `online` is the tower's state (`TowerOnline`, anchored, activated), which implies fuel because an out-of-fuel tower goes offline. A system whose security is unknown gets an offline policy. The owner corporation is `tower.OwnerId` and the owner alliance is `tower.AllianceId`, as the PosMgr standing owner already uses them.
- For each battery of the tower (`ByControlTowerAsync`) that is a player-controlled battery with numbers in its type, places it with `PLACE_BATTERY` holder 0 (online when the tower and the battery are online) and sends `SENTRY_BATTERY` (the tower, or 0 when offline). A battery a pilot holds is skipped: its holder's orders place it, and this pass must not take the holder away.
- Sends the pilot facts as a snapshot: `SENTRY_PILOT` with ship 0 forgets the tower's facts, then one fact for each pilot session of the system (`SessionsInSystem`) whose identity is known.
- Facts come from `SpaceComposition` (`spaceContainers.SentryPilotFactsOf`), which reuses the sources the weapon rules read: the character's affiliation (`AffiliationOfAsync`), its security status (`securityStatusOf`), the owner's war with it (`ReadStructureAsync(...).ActiveWar`), and the owner's contacts (`CorporationStore.CorporationContactsAsync`, or `AllianceContactsAsync` when alliance standings are used). The standing is the first contact entry among character, corporation and alliance, and 0 when there is none, as EveJS's `resolveContactStanding` does. The contact's `RelationshipId` is taken as the standing, as EveJS compares `relationshipID` with the threshold; this is not verified against a client-visible contact value.
- High security is `SpaceSessions.IsHighSecuritySystem` (the SDE security class of the system).
- Triggers: `SynchronizeControlTowerSlimStateAsync` (tower and module state changes, after the force field), a successful `SetTowerSentrySettings` (after the commit), and `SystemEntered` (each control tower of that system). A per-tower gate serializes the reconciles.

Verified (2026-11-19):

- `ControlTowerHostTests.Sentry_policy_batteries_and_pilot_facts_follow_the_tower_and_its_saved_settings_into_the_simulation` (new, `[PgFact]`, `eve_p5`): the online tower's unset policy, the unheld battery placed with holder 0 and bound, the settings saved over the registered PosMgr wire reach the simulation (5.5, -2.5, war, online), the pilot fact snapshot (forgotten first, then the named pilot), and the tower's offline policy by request. Passed.
- `dotnet test dotnet/tests/Eve.Services.Space.Tests -c Release` on `eve_p5` (one run, full): 1273 passed, 0 failed, 0 skipped. `ControlTowerHostTests` alone: 23 passed.
- `dotnet build -c Release -warnaserror` of `Eve.Services.Space.Tests` and `Eve.Host.Tests`: 0 warnings, 0 errors.

Open after slice 2:

- The status-drop receipt is not sourced, so the stored `statusDrop` flag is not on the wire and drives no target (EveJS reads a persisted security-penalty receipt).
- The facts are a snapshot at the triggers above. A standing edit, a security-status change or a war declared or ended between triggers is not re-sent until the next trigger; a pilot who enters the system is covered by `SystemEntered` only for the towers of that system. Until a trigger, the scene acts on the older facts.
- NPC ships are not named (no pilot facts).
- Battery damage, cycle, capacitor and charge are not run (the fire section above).
- Not verified on the graphical V24 client or on a live server (the sentry's visible effect and the battery's firing are not shown to the pilot).

### Control Bunker live item authority: slice 2 blocked (2026-11-18)

No code or migration was added for this slice. The premise does not hold in this checkout, verified as follows:

- Client: `AddInfrastructureHubToScroll` (`eve/client/script/ui/shared/agencyNew/ui/contentPageInfoConts/factionWarfareSystemInfoContainer.py:49-61`, codebase-memory) takes the ID from `facWarMgr.GetControlBunkerIDInSystem()`. It shows the bunker only when `ballpark.GetInvItem(id)` and `GetBall(id)` both exist, and takes the type from the slim item. The client has no online or owner field of its own for the bunker.
- Host: `FactionWarfareControlBunkers.ReadLiveControlBunkerIdAsync` (`dotnet/src/Eve.Host/Persistence/FactionWarfare/FactionWarfareControlBunkers.cs:130-144`) returns the persisted identity only when the simulator reports a live body with group 925 and a finite 3-vector position. `RestoreAsync` (lines 147-192) places a bunker only from that body or from an injected `readVerifiedPosition` provider.
- Production composition passes no position provider: `dotnet/src/Eve.Host/Composition/FactionWarfareComposition.cs:244-245` omits it. So no bunker is placed in production and `GetControlBunkerIDInSystem` returns `None` to the client. The only test that places one (`Control_bunker_lifecycle_uses_sde_state_and_skips_unverified_production_placement`) injects a test anchor.
- Storage: the only bunker column is `fw_systems.control_bunker_id` (migration `0009_fw_control_bunkers.sql`, identity from a sequence in the 80000000 range). A check of `information_schema` on `eve_p5` found no table holding live bunker item rows (the only matches are `factionwarfare.fw_*`, `sde.dynamicItemAttributes`, `trade.staged_items` and `contracts.*item*`).
- Online and owner cannot come from the simulator either: `SimGetSceneBodyReply` carries only item id, type, group, category, position and error. `RestoreAsync` writes `UpkeepState.FullPower` as a constant (lines 179, 186, 203) and the owner from the occupier's militia corporation.

What is needed before a live item row can be authoritative: a verified source for the bunker's position and ball in a system (EveJS or a client fact), and a verified source for its online and owner state. Adding a table or column now would store values that no verified source sets, so it is not done. The DB regression for the existing lifecycle has not been rerun in this session.

### POS force field ball, slice 1 (2026-11-18)

Scope: the force field ball of an online control tower, in the simulation, created and removed by SimLink kinds 180 to 183 (protocol 104). The V24 client reads a tower's field through `playerOwned.py:91-115` (`forceFieldID` and the ball's harmonic, compared with the ship's); EveJS creates the field as an inventory item with `createSpaceItemForOwner` (`controlTowerRuntime.js:935-1032`).

Implemented:

- `crates/eve-space`: `PlaceForceField` / `ForceFieldPlaced` / `RemoveForceField` / `ForceFieldRemoved` in `link` (kind 180 to 183), a `Role::ForceField { tower_id }` deployable in `deployable.rs` (`place_force_field`, `remove_force_field`), and dispatch in `sim/dispatch.rs`. A field is a ball with the scene's usual visibility, removal and watcher handling.
- `dotnet/src/Eve.Space`: the same four records in `SimMessages.cs`, their decode and encode arms, kind constants and a placement validator in `SimCodec.Shared.cs`; `SimCodec.ProtocolVersion` is 104.
- `tools/dev/gen-simlink-fixtures.py`: kinds, encoders, valid and invalid cases for the four messages, and Hello/HelloAck at 104.

Verified:

- `cargo test -p eve-space --lib`: 1064 passed, 0 failed, 0 skipped. Nine of them are new: four link frame tests (`link::force_field_tests`) and five simulation tests (`sim::tests::force_field_tests`): placement and removal, an unchanged re-placement, a replacement id, a refused radius (0, -5, NaN), an id held by another tower, a missing field, and an unknown system.
- `cargo clippy -p eve-space --all-targets -- -D warnings`: clean. `cargo fmt --all` applied.
- `dotnet test dotnet/tests/Eve.Space.Tests`: 574 passed, 0 failed, 0 skipped (3 are new in `ForceFieldCodecTests`).
- Release builds with warnings as errors: `Eve.Space` and `Eve.Space.Tests`, 0 warnings, 0 errors.

Not done in slice 1 (the gaps stay open):

- The golden fixtures were not regenerated at that slice (history): `tools/dev/gen-simlink-fixtures.py` is refused by the command policy in this session, so `oracle/fixtures/simlink/messages.json` and the shared Rust/C# fixture tests do not cover kinds 180 to 183 or version 104. The generator source is updated and must be run to regenerate the file.
- The Host does not send `PlaceForceField` / `RemoveForceField`. The tower-online and tower-offline transitions (`SpaceContainers.ControlTowers.cs`) need a durable item id for the field (EveJS creates a real inventory item); the Host has no allocator for it, and inventing an id would be a fake success. No `ISimulation` / `SimClient` method for the two kinds exists yet.
- The field's category is the default charge category (`category_of`), not the SDE category of the force field type; the radius and the type come from the message.
- Admission and the boundary are slice 2 (below); the harmonic publication and the field's start time are slice 3 (below).

### POS force field admission and boundary, slice 2 (2026-11-18)

Scope: who a force field admits and what happens to a ship it does not admit, in the simulation. Sources: EveJS `starbaseAccessRuntime.js` (`getForceFieldAccess`, `canShipEnterForceField`, `matchesPassword`), `starbaseForceFieldBoundary.js` (`enforceForceFieldMovement`, `resolveDeniedEntry`, `synchronizeShipHarmonic`, `BOUNDARY_CLEARANCE_METERS` 1 m, repulsion 500 to 2 500 m/s), and `forceFieldContainment.js` (group 411 containment, the `centerDistance + shipRadius < fieldRadius` test, which the boundary does not use). The client compares a ship's harmonic with the field's (`playerOwned.py:91-115`).

Implemented in `crates/eve-space/src/scene/force_field.rs`:

- Admission (`Access::admits_group` plus the password set): a ship is admitted by its corporation when the tower allows the corporation and the ids are non-zero; by its alliance when the tower allows the alliance and the id is non-zero; or by a matching ship password. Other ships are not admitted.
- Boundary (`enforce_force_fields`, called from the pilot settle path in `scene/movement.rs`): for each field, the boundary is the field radius plus the ship radius plus 1 m. A move that crosses the boundary is stopped on its surface where it enters. A ship that starts inside is not pulled back, and is pushed out at 500 to 2 500 m/s. A crossing move is always in EveJS's boundary-contact band, so its outward speed is 10% of the inward speed, capped at 100 m/s. An order that pulls the ship back in is cancelled (`Course::Stop`). A ship in warp is not checked; a warp that completes is checked at its end.
- Harmonic (state only): a ship admitted inside a field gets the tower's id as its harmonic, a ship with no admitted field has none. It is held in `Scene::force_field_harmonics` and is told to the client in slice 3 (below).
- Protocol 104, kinds 184 `SetForceFieldAccess` and 185 `ShipForceFieldPassword` (no answer). `Access` is held per tower, so an access set before the field is placed applies to it. A pilot that is no longer in the scene loses its password and harmonic at the next settle.

Verified:

- `cargo test -p eve-space --lib`: 1082 passed, 0 failed, 0 skipped, after a clean rebuild of the package. Slice 2 adds 18 of them: 15 scene tests (`scene::force_field_tests`: the EveJS geometry, admission by corporation, alliance and password, a denied alliance, a field without access, the boundary push and its 100 m/s contact value, the 500 m/s push for a ship inside, the order cancel, the warp exemption, the scene settle path, and the leave cleanup), two link frame tests for kinds 184 and 185 (roundtrip, kinds, bad flags), and one sim test (access and password kept without an answer).
- `cargo clippy -p eve-space --all-targets -- -D warnings`: clean. `cargo fmt --all` applied.
- `dotnet test dotnet/tests/Eve.Space.Tests`: 575 passed, 0 failed, 0 skipped. One is new: `Force_field_access_and_ship_password_roundtrip_over_the_sim_link`.
- Release builds with warnings as errors: `Eve.Space` and `Eve.Space.Tests`, 0 warnings, 0 errors.

Gaps and approximations (not verified against a client):

- Standings admission is not implemented: the pinned EveJS tree does not back it (the rule the task named), so it is recorded here as a gap rather than invented.
- The Host does not send `SetForceFieldAccess` or `ShipForceFieldPassword`, and it cannot place a field yet (the slice 1 gap: no field item id). The Host already has `SetShipPasswordAsync` (`dotnet/src/Eve.Services.Space/SpaceSessions.cs:181-217`), which matches the password with `StarbasePasswordHash` and sets a `harmonic` slim field from the tower's deployable id without any field. That is a separate path from the simulation's harmonic and must be reconciled when the Host is wired.
- A ship that is not in a field's range is never checked, so a field that is not placed does not stop anything. Nothing reaches the client yet: the boundary correction moves the ship in the simulation, and the correction is published only through the existing position updates.
- Approximations: a warp is treated as completed when the flight is gone after a settle (EveJS marks the completion explicitly); the order cancel for `Goto` uses the heading, and for `Follow` and `Orbit` always cancels (EveJS's `targetPoint` is null for an entity target, which is what it does); NPC pilots are checked like player pilots, which EveJS does not confirm for NPC ships.
- The `Role::ForceField` ball still has the default charge category (slice 1).

### POS force field presentation, slice 3 (2026-11-18)

Scope: what the clients that see a force field and its ships are told. Client facts used (V24.01, read with codebase-memory `get_code_snippet` and not run): `PlayerOwned.CompareShipStructureHarmonic` (`playerOwned.py:91-115`) returns true when the field ball's harmonic is not -1 and equals the ship's; `ForceField.Assemble` (`environment/spaceObject/forceField.py:11-23`) reads the slim item's `forcefieldStartTime` to play the field's start animation after 10 s. EveJS shapes: `buildForceFieldBoundaryCorrectionUpdates` and `buildStarbaseHarmonicUpdates` (`destiny/commands/starbaseForceField.js`), and `buildSetBallHarmonicPayload` (`destiny/stream/actions.js:209-226`, `SetBallHarmonic` with entity, harmonic, corporation, alliance, forcefield flag).

Implemented:

- The field's ball carries its tower as its harmonic (`deployable.rs` `ball_harmonic`, in the AddBalls the watchers receive).
- The field's slim item carries `forcefieldStartTime` set to the tick it was placed at (a field placed again starts again).
- A boundary stop is told to the bound pilot and to its observers (`scene/force_field.rs` `publish_force_field`, through the same `tell_split` path as the collision correction): `SetSpeedFraction 0` and `Stop` if the order was cancelled, then `SetBallPosition` if the ship was moved, then `SetBallVelocity`, in EveJS's order.
- A change of a pilot's harmonic is told as `SetBallHarmonic [ship, harmonic, corporation, alliance, 0]`, with -1 for none.
- A field going down and coming back is the existing deployable path: the ball is removed from, or added to, the views of the pilots who see it.

Verified (headless, Rust unit tests on the scene's own output):

- `cargo test -p eve-space --lib`: 1087 passed, 0 failed, 0 skipped after a clean rebuild of the package. Slice 3 adds five: the ball's harmonic, the start time (placed, and placed again), the stop told as position and velocity with no harmonic, the cancelled order told in EveJS's order, and an admitted ship told the tower's harmonic.
- `cargo clippy -p eve-space --all-targets -- -D warnings`: clean.

Not verified and not done:

- No graphical client run, so nothing here is shown to a pilot yet.
- The notification a pilot sees when a field goes down or comes back is not implemented. The client events for a tower's state come from the Host through `posState` and `OnStructureFullyOnline` (`playerOwned.py:73-89`); no client event named for a force field going down was found (codebase-memory: `forceField.py`, `moonmining.py`, `menusvc.py` `EnterForceFieldPassword` only). The Host does not place fields yet, so there is nothing to notify.
- A ship's harmonic is told only when the boundary runs on a move and the harmonic changes. A pilot who is already inside an admitted field when another pilot first sees it is not given the harmonic in its first ball (its ball is sent with no harmonic), and the `SetBallHarmonic` argument shape is EveJS's, not a client-verified one.
- A pilot's harmonic is kept per session; a ship that is not moved is not re-told, which matches EveJS only as far as its move-driven sync goes.
- The field's category is still the default charge category (slice 1).

### POS force field Host wiring, slice 4 (2026-11-19)

Scope: the Host gives an online control tower its force field and sends the admission state, so slices 1 to 3 reach the simulation. The gap this closes was the missing durable item id: the field is now a real inventory item, as EveJS creates it (`controlTowerRuntime.js:935-1032`, `TYPE_FORCE_FIELD` 16103, group 411, name "Force Field", `ATTRIBUTE_SHIELD_RADIUS` 680).

Implemented:

- Persistence: `SpaceItemKind.ForceField` (6) in `PgSpaceItemStore.cs`, and migration `0034_force_field_kind.sql`, which widens the `space_items_kind_valid` check to 0 to 6 and adds a partial unique index on `launcher_id` where `kind = 6`, so a tower has at most one field item. The field item is created with `PgItemStore.CreateAsync` and its space row written with `PgSpaceItemStore.SaveAsync`. Its launcher is the tower, its corporation and alliance are the tower's, its radius is the tower type's SDE `shieldRadius` (30 000 m for the Amarr Control Tower, type 12235), and its type is 16103.
- Reconcile (`SpaceContainers.ControlTowers.cs` `ReconcileForceFieldAsync`): run by `SynchronizeControlTowerSlimStateAsync` after the state transaction commits, and by `RestoreDeployedAsync` when a system opens. An online tower gets one field item (kept on a repeat sync) and `PlaceForceField`. Any other state deletes the item and sends `RemoveForceField`, and a removal the simulation answers with `FORCE_FIELD_NOT_IN_SPACE` counts as done. A placement the simulation refuses is an error (`InvalidOperationException`) and the item stays, so the next sync retries.
- Triggers covered: the online job (`CompleteControlTowerOnlineAsync`), the hourly fuel job when the bay cannot pay, explicit offline (`OfflineControlTowerAsync`), and restart.
- Simulation interface: `ISimulation.PlaceForceFieldAsync` and `RemoveForceFieldAsync` (answers matched by field id and tower id in `SimClient`), and `SetForceFieldAccess` (kind 184) and `SetShipForceFieldPassword` (kind 185), both without an answer.
- Access: `SetTowerPassword` sends `SetForceFieldAccess` after its commit, with the corporation and alliance of the tower and the access flags it saved. A password of `false` keeps the saved password, as before.
- Ship password: `SpaceSessions.SetShipPasswordAsync` now matches the password once against every control tower of the system. The harmonic slim field is written once from the first online match (as before), and the simulation receives `SetShipForceFieldPassword` for each tower with that same match, so the two cannot disagree.

Verified (PostgreSQL `eve_p6`, `EVE_SDE_DIR` of the pinned SDE, Release, warnings as errors):

- `ControlTowerHostTests`: 20 passed, 0 failed, 0 skipped. Three are new: `Force_field_follows_its_online_tower_once_and_is_taken_out_when_the_tower_goes_offline_by_fuel_or_by_request` (one item, SDE radius and type, repeat sync keeps the id, restart re-places it, fuel-out removal, online again, explicit offline removal), `A_field_the_simulation_refuses_is_an_error_and_is_placed_again_once_the_simulation_accepts_it`, and `Tower_access_reaches_the_simulation_each_time_the_posMgr_password_is_saved_over_host_wire` (real posMgr Host wire).
- `SetShipPasswordHostTests`: 1 passed, 0 failed, 0 skipped. The existing registered-route test gained assertions: one harmonic write per call, one `SetShipForceFieldPassword` matching the tower, a refused match for the wrong password, and none for an invalid password. Its existing assertions are unchanged.
- Release build of `Eve.Services.Space.Tests` with `TreatWarningsAsErrors`: succeeded, 0 warnings.

Not verified and not done:

- The full `Eve.Services.Space.Tests` project on `eve_p6` (Release, warnings as errors): 1270 passed, 0 failed, 0 skipped (8 m 13 s).
- `Eve.Host` and `Eve.Host.Tests` were not built or run for this slice.
- `oracle/fixtures/simlink/messages.json` was not regenerated at that slice (history) (the generator is not run, per the session's policy).
- At slice 4 a tower's password change did not re-match pilots that already held a credential (superseded by slice 5): `SpaceSessions` keeps only the SHA-256 digest of each pilot's password, so it cannot match against the new tower hash. Those pilots are matched again only when they set their password.
- The simulation's per-pilot harmonic (`SetBallHarmonic`, slice 3) and the ship's `harmonic` slim field (the Host's write) remain two separate paths, each written once per event; they are not unified.
- Standings admission remains a documented gap (slice 2).
- No graphical client run.

### POS force field password re-match, slice 5 (2026-11-19)

Scope: a tower's saved password changes while pilots already hold a ship password. Client fact (V24.01, `eve-logic` `playerOwned.py:182-191`, `EnterShipPassword`, read with codebase-memory `get_code_snippet`): the client sends the plaintext ship password only from its prompt, so the Host can never be sent it again. The digest is therefore the only credential kept, as before.

EveJS fact (`/home/user/evejs-261108.1/server/src/services/starbase/starbaseAccessRuntime.js:197-263`, read with the Read tool): the credential keeps `sha256(password)` and the match is `scrypt(hex(digest), salt)` against the tower's saved hash, so a new saved hash is checked again from the same digest. The Host's `StarbasePasswordHash` (`dotnet/src/Eve.Services.Space/StarbasePasswordHash.cs`) uses the same scheme (`scrypt` N=16384, r=8, p=1, 32 bytes, with the lowercase hex digest as the input), so no plaintext is needed.

Implemented:

- `StarbasePasswordHash.MatchesDigestAsync` and `DeriveFromDigestAsync` take the raw SHA-256 digest. The password overloads delegate to them, so stored hashes are unchanged.
- `SpaceSessions.ShipPasswords.cs`: `MatchShipPasswordAsync` is the one per-tower match, and every tower's answer is returned. `SetShipPasswordAsync` uses it.
- `RematchShipPasswordsAsync(systemId)` runs for each pilot in the system that holds a credential for its current ship. It matches the kept digest again and sends `SetShipForceFieldPassword` for each tower. The harmonic follows the simulation's answer (slice 6). A pilot whose credential changed meanwhile is skipped.
- `SpaceContainers.SetTowerPasswordAsync` calls it after the commit when the saved hash changed (a password of `false` does not).

Verified (PostgreSQL `eve_p6`, Release, warnings as errors):

- `ControlTowerHostTests.Tower_password_change_matches_a_pilot_holding_a_ship_password_again_from_its_kept_digest`: 1 passed, 0 failed, 0 skipped. Set "field-secret" (harmonic = tower, simulation told a match), change the tower to another password (harmonic -1, simulation told a refusal), set it back (harmonic = tower again). The posMgr wire carries every tower change.
- `ControlTowerHostTests` class: 21 passed, 0 failed, 0 skipped.
- `SetShipPasswordHostTests`: 1 passed, 0 failed, 0 skipped (the existing route test, unchanged assertions).
- Release build of `Eve.Services.Space.Tests` (with its project graph) with `TreatWarningsAsErrors`: 0 warnings, 0 errors.

Slice 6 (below) makes the simulation the only source of the harmonic.

### POS force field harmonic, one source, slice 6 (2026-11-19)

Source of truth: the simulation. `Scene::force_field_harmonics` holds each pilot's harmonic (the tower of the admitted field it is inside, else none). The settle path decides it where the boundary runs, and `refresh_force_field_harmonic` and `refresh_all_force_field_harmonics` recompute it without a move when an input changes: a ship password (kind 185), a tower's access (kind 184), or a field placed or removed.

Both paths read that one value. The clients are told `SetBallHarmonic` as before (`publish_force_field`). The Host writes the ship's slim `harmonic` only from the simulation's report `ShipForceFieldHarmonic` (kind 186, simulation to host, protocol 105), in the order the simulation tells them (one serial chain, `SpaceSessions.ShipPasswords.cs`). The Host chooses no harmonic any more: `SetShipPassword` and the re-match send only the per-tower matches.

Behaviour change: before, the Host set the slim harmonic at password time whether or not the ship was inside a field, and it chose the first online match itself. Both are removed. The harmonic now follows the field the ship is inside, as the scene has modelled it since slice 2.

Verified:

- `cargo test -p eve-space --lib`: 1093 passed, 0 failed, 0 skipped. New: scene `a_password_change_inside_the_field_takes_effect_at_once_and_the_host_is_told` and `a_ship_admitted_inside_that_leaves_the_field_loses_its_harmonic_and_the_host_is_told`; sim `a_password_through_the_simulation_tells_the_host_the_pilot_harmonic` and `a_field_going_up_or_down_around_an_admitted_pilot_changes_its_harmonic_at_once`; link `ship_force_field_harmonic_frame_roundtrips_and_refuses_a_harmonic_that_is_not_a_tower_or_none`. The pinned `PROTOCOL_VERSION` assertion in `sim/tests.rs` moves to 105.
- `cargo clippy -p eve-space --all-targets -- -D warnings`: clean. `cargo fmt --all` applied.
- Host, `ControlTowerHostTests`: 21 passed, 0 failed, 0 skipped. `Tower_password_change_rematches_a_pilot_and_the_ship_harmonic_follows_the_simulation_answer` checks that the slim harmonic is written once per simulation answer, in order, and that the Host writes nothing before the first answer. `SetShipPasswordHostTests`: 1 passed, 0 failed, 0 skipped (the same setup; the Host waits for the simulation's answer).
- `Eve.Space.Tests`: `ForceFieldCodecTests` round-trips kind 186 and refuses a harmonic of 0 or below -1.
- Release builds with warnings as errors: `Eve.Services.Space.Tests` (with `Eve.Space`, `Eve.Services.Space`, `Eve.Host`) and `Eve.Space.Tests`: 0 warnings, 0 errors.

Known failure at this slice (history; the fixtures were regenerated later, see the current state at the top): `SimCodecTests.The_fixtures_cover_every_message_kind` fails. Its expected kind list (added in `59246fed8`, extended here with `ShipForceFieldHarmonic`) names the force-field kinds 180 to 186, but `oracle/fixtures/simlink/messages.json` (version 102) was last regenerated at `76d433e22` and has none of them. The generator source (`tools/dev/gen-simlink-fixtures.py`) is updated for protocol 105, kind 186 and the Hello version, but the generator was not run (the orchestrator regenerates it), and the golden file was not edited by hand. Until it is regenerated this test fails. `Eve.Space.Tests`: 585 passed, 1 failed (that test).

## POS force field client notification, slice 7 (2026-11-19): not found

Search of the V24.01 client (`/home/user/eve-logic`, codebase-memory `search_code` and `get_code_snippet`) for the force field harmonic and the starbase state:

- A tower's online and offline state reaches the client as the slim `posState` and `posTimestamp`. `PlayerOwned.StalledOnlineEvent` (`playerOwned.py:73-89`) reads `item.posState`, then calls `sm.ScatterEvent('OnStructureFullyOnline', itemID)` and `OnSpecialFX` with `effects.StructureOnlined`. Both are client-local scatters, not wire events. The Host already writes `posState` on every online and offline transition: `SynchronizeControlTowerSlimStateAsync` (`SpaceContainers.ControlTowers.cs:603-622`) through `SetControlTowerSlimState` (`:907-911`).
- The force field's own client code, `forceField.ForceField.Assemble` (`environment/spaceObject/forceField.py:11-23`), reads only `forcefieldStartTime` from the slim item (written since slice 3). The searches for force-field, harmonic and forcefield names found no force-field-specific event, effect or scatter: only `CompareShipStructureHarmonic`, `EnterShipPassword`, `EnterTowerPassword`, `EnterForceFieldPassword`, `OfferSetNewPasswordForForceField` and the moon-mining `SaveForcefield` UI.
- No offline effect exists: there is no `effects.StructureOffline` name in the client.
- `StructureBrowserController.OnStructureStateChanged` (`structureBrowserController.py:102-108`) updates only structures in its own dictionary, keyed by `structureID`. A POS tower is not in that dictionary, so the client ignores it.
- eve.rs sends `OnSpecialFX` only for tether effects (`SpaceSessions.Tether.cs:250`). The client's `Park.OnSpecialFX` (`michelle.py:1552`) forwards that to a local scatter.

Conclusion: the V24.01 client has no wire event for a force field going down or coming back. The field is seen as its ball being removed from, or added to, the destiny stream (`PlaceForceField` and `RemoveForceField`), and its start animation follows `forcefieldStartTime`. The online and offline notification the client does consume, the slim `posState`, is already sent on every transition. No Host notification was added, and no Host test was written for an event that does not exist. Still unverified: what a pilot sees on a graphical client when the field goes down or comes back.

## Open items

- **POS force field and password admission:** the Host now places and removes an online tower's field and sends its access and ship-password state (slice 4, migration 0034). A tower's password change re-matches pilots holding a credential from the kept digest (slice 5). Still open: the client-visible force-field presentation is not verified on the graphical client; standings admission is not implemented; the harmonic is one source (slice 6), and a field going down or up has no V24.01 client event (slice 7, not found; the tower's `posState` is already sent).
- **Starbase OBO target calls:** `AddTargetOBO` and `RemoveTargetOBO` are implemented on the dogma location (2026-11-18, see the battery sections above), with the battery's target lock state in the Rust scene and the battery control grant (`AssumeStructureControl` / `RelinquishStructureControl`, see POS battery control grant below).
- **Sentry targeting:** the stored policy drives a battery with no holder in the scene (protocol 106, the sentry sections above). Open: the status-drop receipt, damage and cycle, and re-sending the facts when a standing, security status or war changes between triggers.
- **Other POS inventory/use consumers:** silo `viewput`/`viewputtake` is enforced. Ship-maintenance, reprocessing/compression arrays and jump-bridge `use` behavior remain incomplete.
- **Moon/reaction production:** module onlining/resource accounting and silo-to-silo transfer are implemented, but historical moon output is not configured in the pinned EveJS legacy authority, and the SDE contains no historical moon abundance. The server does not fabricate output.
- **Control Bunker:** `GetControlBunkerIDInSystem` remains `None`. The client at `eve/client/script/ui/shared/agencyNew/ui/contentPageInfoConts/factionWarfareSystemInfoContainer.py:49-60` requires a live inventory item and ball. The pinned SDE has bunker types but no per-system live item IDs/positions, and the matching EveJS tree has no live bunker placement authority. Do not synthesize an ID or position.

These gaps remain `PARTIAL-TESTED` or open in [`EVE_RS_COVERAGE.csv`](service-audit/EVE_RS_COVERAGE.csv) and [`DEVIATIONS.md`](../reference/compat/DEVIATIONS.md). The control-bunker evidence is in [`DEEPCHECK_facWarMgr.md`](service-audit/DEEPCHECK_facWarMgr.md); POS client/EveJS row traces are in [`posmgr-partial-audit-2026-11-13.md`](posmgr-partial-audit-2026-11-13.md).

## Verification limits

Registered Host/PostgreSQL coverage and simulator slim-state assertions are headless checks. A graphical V24 client was not launched, and no complete same-scenario EveJS POS run was executed. The exact local pass counts and build results follow.

## Verification (2026-11-17)

Verified:

- `IndustryFacilityHostFlowTests`: 17 passed, 0 failed, 0 skipped on PostgreSQL `eve_p4`.
- Related Host industry classes (`IndustryJobPersistenceTests`, `IndustryJobsHostWireTests`, `IndustryHostRpcTests`, `IndustryFacilityManagerTests`, `IndustryJobNotificationWireTests`, `IndustryActivityPersistenceTests`, `IndustryCorporationPersistenceTests`): 29 passed, 0 failed, 0 skipped on `eve_p4`.
- `IndustryJobsServiceTests` and `PosMgrServiceTests`: 23 passed, 0 failed, 0 skipped.
- `StructureCompositionTests` and `StructurePosHostWireTests`: 21 passed, 0 failed, 0 skipped on `eve_p4`.
- `CommandBurstHostTests` and `SpaceContainerTests`: 51 passed, 0 failed, 0 skipped on `eve_p4`.
- `ControlTowerHostTests`: 10 passed, 0 failed, 0 skipped on `eve_p4`.
- Release builds with warnings as errors for `Eve.Host`, `Eve.Services.Core`, `Eve.Services.Space`, and their three test projects: 0 warnings, 0 errors.
- `cargo fmt --all --check`, `cargo check --workspace`, and `cargo clippy --workspace --all-targets -- -D warnings` passed. No Rust source changed.
- The profile JSON content matches the pinned EveJS profile file; the checked-in Git blob uses LF line endings while the source file uses CRLF. `git diff --check` passed.

Not checked:

- Graphical V24 acceptance, a POS-specific headless-client walk, and a complete paired EveJS/eve.rs POS run.
- Force-field ball lifecycle/admission, OBO targeting, sentry control, bunker placement, and hub-upgrade power/fuel behavior; these remain open as listed above.
- Repository-wide CI for the uncommitted worktree.
