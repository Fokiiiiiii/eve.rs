# POS sentry adversarial review (branch haiku7)

Scope: the sentry in `crates/eve-space/src/targeting/sentry.rs` and the sentry use of `apply_fire` in `crates/eve-space/src/targeting/battery.rs`; the
SimLink kinds 190 to 194 (`link/kind.rs:38-60`, `link/decode_message.rs`, `link/mod.rs`, `sim/dispatch.rs`) and their C# codec
(`dotnet/src/Eve.Space/SimCodec.Encode.cs`, `SimMessages.cs`); the Host sentry (`dotnet/src/Eve.Services.Space/SpaceContainers.Sentry.cs`), battery
fire and offline paths (`SpaceContainers.Batteries.cs`), the POS control grant (`SpaceContainers.PosMgr.cs`, `dotnet/src/Eve.Ships/PgDeployedStore.cs`),
the tower offline and unanchor path (`SpaceContainers.ControlTowers.cs`), the pilot facts provider (`dotnet/src/Eve.Host/Composition/SpaceComposition.cs`),
and the Dogma battery branch (`dotnet/src/Eve.Services.Dogma/DogmaIMService.Modules.cs`, `DogmaIMService.Targeting.cs`).

Reference limits: the EveJS reference (`/home/user/evejs-261108.1`, `evaluateSentryTargetPolicy` in `starbaseDefenseController.js`) is outside the
project root, so Serena could not read it and it is not indexed; a shell read of it was blocked. The rules were therefore checked against
`docs/project/pos-force-field-and-modules.md:111` (the client options, and "Both thresholds are hidden in high security") and the in-code doc comments.
`evaluateSentryTargetPolicy` itself was not compared line by line.

## 1. Confirmed defects, fixed

Each was reproduced first by a failing Host test (PostgreSQL-backed `ControlTowerHostTests`), then fixed minimally, and committed on its own.

| ID | Defect | Where (before fix) | Commit | Failing-first evidence |
|----|--------|--------------------|--------|------------------------|
| R-01 | A battery a pilot still holds (grant in the database) stays bound to its tower's sentry after the tower goes offline and back online, so the sentry drives it. | `SpaceContainers.Sentry.cs:62-65` (held branch skipped, binding left); cause `SpaceContainers.Batteries.cs:219-240` (`OfflineBatteriesAsync` places the battery with holder 0, and `targeting/battery.rs:215` clears the holder) | `e74624295` | last `SimSentryBattery` for the battery named tower `1990000004`, expected `0` |
| R-02 | `RelinquishStructureControl` releases the grant in the database but tells the simulation nothing, so the battery keeps its holder session, its locks and its fire, and the tower's sentry stays blocked. | `SpaceContainers.PosMgr.cs:337-349` (no simulation update after the grant is released) | `791035ea6` | last `SimPlaceBattery` still carries holder session `1`, expected `0` |
| R-03 | A pilot leaving the cluster releases its battery grants in the database with no simulation update, so the same stale holder, locks and fire remain. | `SpaceContainers.PosMgr.cs:353-360` (`ReleaseBatteryControlAsync`); `dotnet/src/Eve.Ships/PgDeployedStore.cs:160-178` (rows deleted, nothing returned) | `a60cba1ee` | last `SimPlaceBattery` still carries holder session `1`, expected `0` |

Traced paths:

- R-01: a tower goes offline (`DeactivateAsync`) and `SynchronizeControlTowerSlimStateAsync` (`SpaceContainers.ControlTowers.cs:603`, called from
  `SpaceComposition.cs:581`) calls `OfflineBatteriesAsync`, which places the battery offline with no holder (`SpaceContainers.Batteries.cs:238-240`).
  The simulation clears the holder (`battery.rs:215`). When the tower is onlined again (`SpaceComposition.cs:595`), `ReconcileSentryAsync` sees
  the grant (`BatteryControllerAsync > 0`) and skips the battery without unbinding it, so `sentry_pass` (`sentry.rs:117`, holder is `None`) drives
  a battery a pilot still controls. Fix: the held branch now sends `SimSentryBattery(battery, 0)` before skipping it (`Sentry.cs:62-65`).
- R-02: `RelinquishStructureControl` (`PosMgr.cs:93`) deletes the grant and commits. Before the fix nothing reached the simulation, so
  `place_battery` never ran with holder `0` (`battery.rs:185`, release at `battery.rs:205`). Fix: after a real release, the battery's tower is
  reconciled (`PosMgr.cs:344-348`), which places unheld batteries with no holder.
- R-03: `ReleaseBatteryControlAsync` is called on leaving the cluster (`SpaceComposition.cs:616`). Fix: the store returns the control towers of the
  released batteries (`PgDeployedStore.cs`), and each is reconciled (`PosMgr.cs:353-360`).
- R-04 (fixed 2026-11-19): a stop of a battery's fire was refused on an offline battery (`BATTERY_OFFLINE`) and on an unheld one (`NO_CONTROL_GRANT`), against the rule in the `battery_fire` documentation that a stop is always accepted. The scene now accepts a stop whatever the battery state (`targeting/battery.rs`, `sim/dispatch.rs`; commit `d9f30a7f9`). The Host sends a stop without a placement, which names a holder, so the holder's stop and the stop of an unheld battery are accepted, and another pilot's stop of a battery that is held stays refused with `NO_CONTROL_GRANT` (`SpaceContainers.Batteries.cs`; commit `f5919a3af`). Tests, both written to fail first: `a_stop_is_always_accepted_for_an_offline_unheld_or_absent_battery` (Rust, `BATTERY_OFFLINE` before the fix) and `Battery_fire_stop_is_accepted_for_an_offline_or_unheld_battery_and_refused_for_another_pilots_held_one` (Host, `BATTERY_OFFLINE` before the fix). The `battery_fire` rule is now the one the document states.


## 2. Open findings, not fixed

| ID | Finding | Status and reason |
|----|---------|-------------------|
| R-04 | Fixed on 2026-11-19 (section 1): a stop of a battery's fire is always accepted, in the scene, in the Host and on the Dogma path. | `crates/eve-space/src/targeting/battery.rs`, `sim/dispatch.rs`, `dotnet/src/Eve.Services.Space/SpaceContainers.Batteries.cs` | `d9f30a7f9`, `f5919a3af` | the stop tests listed in section 1 |
| R-05 | Time-of-check race in the sentry reconcile. `ReconcileSentryAsync` reads the grant (`Sentry.cs:62`) before it takes the battery gate. `BatteryFireAsync` and `BatteryTargetAsync` also read the grant (`Batteries.cs:66`, `:163`) before the gate. If a pilot's grant and holder placement land between the sentry's read and its holder-`0` placement, the simulation ends with no holder while the grant stands, and the sentry binds the battery (same outcome as R-01). | Plausible, reasoned from the code; not reproduced. A deterministic test needs an interleaving seam that the rig does not have. The fix would re-read the grant inside the gate. Not fixed without a failing test. |
| R-06 | Pilot facts are a snapshot at each reconcile (`Sentry.cs:89-102`). A standing, security status or war change during a visit is not re-sent until the next trigger. | Known and open: `docs/project/TODO.md:2658` lists it. Not a new defect. |
| R-07 | The offline placement clears the holder in the simulation while the grant stands (by design: `ControlTowerHostTests` "is placed offline in the simulation ... with no holder"). After the tower is online again the pilot's next order re-places the holder. | Residual, not a defect. Noted because the window between online and the next pilot order has no holder in the simulation. |
| R-08 | Outside the sentry, pre-existing: `RemoveDeployed` for a control tower or a module calls `scene.remove_field` (`crates/eve-space/src/deployable.rs:1009-1013`), which removes only cyno, warp-source, inhibitor and beacon roles. A tower's body therefore stays in the Rust scene after it is unanchored (`SpaceContainers.ControlTowers.cs:1057`, `:364`). | The sentry is safe because a tower can only be unanchored while offline (`ControlTowers.cs:1002-1017`), so its policy is offline and nothing fires. Its pilot facts remain until a reconcile of that tower. Scene removal for towers is an owner decision, so it is not changed. |

## 3. Suspects rejected

| Suspect | Reason |
|---------|--------|
| Sentry attacks its own corporation or alliance | Rejected. `names()` excludes the owner corporation, and the owner alliance when it is not 0 (`sentry.rs:40-44`). The owner ids passed are the tower's owner corporation and alliance (`Sentry.cs:52-53`, `:98`). `DeployedRecord.OwnerId` is the owning corporation: `ControlTowers.cs:795` compares it with the corporation id. |
| High security rules the wrong way round | Rejected. War is checked before the high-security gate (`sentry.rs:45-47`), and the standing and security-status thresholds apply only outside high security (`sentry.rs:48-50`). This matches the client rule in `docs/project/pos-force-field-and-modules.md:111`. `war_names_a_pilot_even_in_high_security_and_the_thresholds_do_not` covers it. |
| Standing or security threshold with the wrong sign or boundary | Rejected for direction and strictness: `pilot.standing < threshold` (`sentry.rs:51`) and `value < threshold` (`sentry.rs:54`) match the client wording "lower than" and "below". Gap: no test pins equality at the threshold (see section 5). |
| War flag or standing precedence in the Host | Rejected. The standing is the character's entry, then its corporation's, then its alliance's, default 0 (`SpaceComposition.cs:503-528`). `ActiveWar` is read with the owner corporation and the pilot character (`PersistenceWeaponTargetContextReadModel.cs:110`). |
| Pilot without facts, or NPC ships, become targets | Rejected. Only pilots with facts are candidates (`sentry.rs:86-105`), and a pilot with no identity gets no facts (`Sentry.cs:91-96`). |
| Out-of-range or invisible targets | Rejected. Each candidate, and each held lock on every pass, must pass `battery_check` at `now` (`sentry.rs:86-105`, `battery.rs:296`, settle at `battery.rs:428`). |
| A holder's battery retargeted by the sentry, in the simulation | Rejected for the simulation: `sentry_pass` skips a battery with a holder (`sentry.rs:117`). The Host-side cases are R-01 to R-03. |
| Firing flag survives lock loss or offline | Rejected. Offline (`battery.rs:93`), holder change (`battery.rs:105`, `:205`), sentry let-go (`sentry.rs:154`), sentry drop (`sentry.rs:164`) and settle release all clear `firing`. |
| Non-finite numbers accepted | Rejected. Decode refuses a non-finite present threshold (`decode_message.rs:197-205`) and a non-finite pilot standing (`decode_message.rs:2849-2865`); `place_battery` refuses non-finite values (`battery.rs:186-197`); the C# codec throws (`SimCodec.Encode.cs:14-19`); `OptionalFiniteNumber` and the -10..10 range apply at the input (`SpaceContainers.PosMgr.cs:113-118`, `:1138`). |
| Ids parsed wrongly, or layout mismatch between Rust and C# | Rejected. Ids are i64, and the solar system i32, on both sides (`decode_message.rs:2821-2870`, `SimCodec.Encode.cs:1552-1563`, `SimMessages.cs:1290-1296`). A ship id of 0 is the clear and maps to `None` (`sim/dispatch.rs:2163`). The byte offsets of policy and pilot are checked by `SentryCodecTests` and `link/sentry_tests.rs`. |
| Notifications to the wrong session | Rejected. The Rust `OnTargetOBO` goes to the holder captured at settle or release (`battery.rs:105`, `:428`); the Host places the battery with the caller's session (`Batteries.cs:180`) after checking that the grant is the caller's (`Batteries.cs:163-167`). |
| Sentry state left behind on unanchor or unload | Rejected for firing, see R-08 for the residual. The tower is offline before unanchor, its offline sync publishes the policy offline and offlines its modules (`ControlTowers.cs:603-633`), and a policy that is not online lets go of the sentry locks (`sentry.rs:107-118`). A reconcile on the next ship entry republishes the sentry (`SpaceContainers.cs:697`). |
| Fire-and-forget system reconcile hides errors | Rejected. `ReconcileSystemSentriesAsync` catches and logs errors (`Sentry.cs:109-125`). |
| Dogma `Activate` or `Deactivate` routes a ship module to the battery path | Rejected as reachable. `BatteryFireAsync` refuses with `NO_CONTROL_GRANT` before it checks that the item is a battery (`Batteries.cs:143`), so a session without a character would get the battery refusal for a ship module. A logged-in pilot always has a character, so this is minor ordering only. |
| Battery online during onlining (Host fire path) versus sentry online only | Rejected as a defect. The holder path treats `TowerOnlining` as online (`Batteries.cs:72`, `:174`), and the sentry requires `TowerOnline` (`Sentry.cs:67`). The sentry is the stricter rule, and it is consistent with the policy's own online rule. |

## 4. Tests

- Failing first, then passing, one commit each: `ControlTowerHostTests.A_battery_a_pilot_still_controls_is_no_sentrys_when_its_tower_comes_back_online`
  (R-01), `Relinquishing_a_battery_takes_its_holder_out_of_the_simulation_so_its_sentry_may_take_it_again` (R-02), and
  `A_pilot_leaving_the_cluster_takes_its_batteries_out_of_the_simulation` (R-03). The tests were not weakened.
- `ControlTowerHostTests` class on the final HEAD: 27 passed, 0 failed, 0 skipped (24 existing plus the 3 new).
- `cargo test -p eve-space --lib sentry` (Rust, no Rust code changed): 19 passed, 0 failed.
- `Eve.Services.Space.Tests` full project on the final HEAD: 1277 passed, 0 failed, 0 skipped (9 m 21 s).

## 5. Gaps in the tests (they still pass, and they do not fail on these rules)

- No test pins the boundary: a standing equal to the threshold, or a security status equal to it, is not named (`sentry_tests.rs`).
- R-04 had no test for a stop; the two tests named in section 1 cover it now.
- No Host test covers the grant-read race (R-05).

## 6. Full-project run and what was not run

Full `Eve.Services.Space.Tests` on the final HEAD: 1277 passed, 0 failed, 0 skipped.

### Not run

- The Host test projects other than `Eve.Services.Space.Tests` (for example `Eve.Host.Tests`, `PosMgrClientRpcHostWireTests`).
- The full Rust workspace, clippy and the other `eve-space` suites (only the `sentry` filter was run).
- No generator was run, no SimLink kind was added, and the protocol is unchanged.
