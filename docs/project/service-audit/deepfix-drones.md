# Deep fix: capacitor warfare

## Decision and scope

This increment fixes one confirmed capacitor-warfare defect: a target with energy warfare resistance had neutralizer strength reduced in both Host and Rust, while drone neutralizers entered the same Rust projection path without Host-side scaling. Resistance now has one owner: the simulation. Both player-ship and drone neutralizer/Nosferatu effects use the target's energyWarfareResistance in Scene::project; Host sends their unscaled amounts. Wire shape, activation errors, persistence, and notification order are unchanged.

This handoff covers the capacitor-warfare path below. Drones' other command/lifecycle flows, ECM, and remote repair were not audited in this increment.

## Whole-client trace

- The V24.01 client activates a dogma effect with its environment, including the target: /home/user/eve-logic/eve/client/script/dogma/clientDogmaLocation.py:401-407 (DogmaLocation.Activate). There is no separate capacitor-warfare RPC response or client error added by this fix.
- The capacitor HUD reads current and maximum charge, computes the displayed proportion, and scatters OnCapacitorChange: /home/user/eve-logic/eve/client/script/ui/inflight/shipHud/capacitorContainer.py:142-149.
- Consumers of that notification found across the client: the ship alert subscribes at /home/user/eve-logic/eve/client/script/ui/inflight/shipAlert.py:31-32 and handles a falling capacitor level at shipAlert.py:142-146; audio forwards it at /home/user/eve-logic/carbon/client/script/environment/audioService.py:482-483 and ShipHealthNotifier processes the percentage at /home/user/eve-logic/eveaudio/shiphealthnotification.py:54-56; nodegraph subscribes and emits its event at /home/user/eve-logic/nodegraph/client/events/ship.py:128-132. The HUD tooltip also reads the same authoritative charge at capacitorContainer.py:58-78. Whole-source Serena search found these as the client OnCapacitorChange sites.
- Rust emits capacitor-change output from take_capacitor after applying the draw (crates/eve-space/src/modules/effects.rs:863-870). Neutralizer applies on cycle start; Nosferatu transfers on cycle end. This timing is retained.
- EVE University describes neutralizer application on activation and Nosferatu transfer at cycle end ([Capacitor Warfare](https://wiki.eveuniversity.org/Capacitor_Warfare#Energy_Neutralizers), neutralizer section around line 253 and Nosferatu section around line 337 in the reviewed page). The same guide says capacitor batteries resist incoming neutralizers and Nosferatus (section around line 373). The simulation test uses 50% resistance and checks a 40 GJ neutralizer removes 20 GJ and a 25 GJ Nosferatu removes 12.5 GJ.
- Current EveJS 261028.1 sets the target resistance attribute for both neut and nos modules in /home/user/evejs-261028.1/src/server/src/space/modules/hostileModuleRuntime.js:225-235. Its resolver reads the target's attribute at hostileModuleRuntime.js:586-595 and applies resistance/range/signature factors in hostileModuleRuntime.js:675-695. This confirms one resistance multiplier for either module.
- eve.rs sends resistance through protocol 27's SimSensors.EnergyWarfareResistance (dotnet/src/Eve.Space/SimMessages.cs:204; encoded at dotnet/src/Eve.Space/SimCodec.Encode.cs:754). Before this fix, SpaceSessions.Modules.cs:270-277 weakened both module actions before SimLink, and crates/eve-space/src/modules/effects.rs also weakened neutralizers. That made a ship neutralizer apply resistance twice and meant a drone neutralizer, which reaches Scene::project through crates/eve-space/src/drones/fight.rs:226-229, bypassed the Host multiplier.
- The Host now returns no resistance attribute for SimRemote.Neutralize / SimRemote.Drain (dotnet/src/Eve.Services.Space/Effects/RemoteResistance.cs), so the module request remains unscaled. The simulation applies the attribute once to both actions in crates/eve-space/src/modules/effects.rs:345-359 using the target ship's spec field (crates/eve-space/src/modules/effects.rs:580-584). Drone landing calls the same Scene::project at crates/eve-space/src/drones/fight.rs:226-229. Neutralizing strikes remain in their existing simulation-owned path.

## Regression checks

- modules::tests::a_neutralizer_is_weakened_by_resistance_and_immunity_keeps_the_capacitor proves a 50% resistance and a zero-resistance immunity case.
- modules::tests::a_nosferatu_takes_from_a_ship_that_holds_more_than_it_does_and_nothing_from_one_that_holds_less proves 50% resistance with the existing capacitor-ratio and cycle-end rules.
- RemoteResistanceTests confirms Host leaves neutralizer/Nosferatu amounts unscaled.
- Other adjacent module effects and charge notifications continue through the existing shared simulation path; no wire/error shape was changed.

## Local verification

- cargo +1.94.1 fmt --all -- --check — passed.
- cargo +1.94.1 check -p eve-space --lib — passed.
- Focused neutralizer and Nosferatu Rust tests — passed (1 each).
- cargo +1.94.1 test -p eve-space modules::tests:: — 160 passed, 0 skipped.
- cargo +1.94.1 clippy --workspace --all-targets -- -D warnings — passed.
- MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Services.Space.Tests -p:UseSharedCompilation=false — passed, 0 warnings/errors.
- RemoteResistanceTests — 4 passed, 0 skipped.
- On eve_p3, via tools/dev/db-lock.sh, RepairHostTests|ModuleEffectsTests — 36 passed, 0 skipped.

## Remaining verification

No graphical-client session was used. The other drone, electronic-warfare, and remote-repair flows remain outside this increment. Code is committed as e1d4eb28 after merging origin/main at ac348916; push awaits the red gate's in-progress CI run 41371810281.
