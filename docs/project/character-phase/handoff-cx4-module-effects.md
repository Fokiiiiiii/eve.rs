# Module effects handoff (cx4)

## Changes

- Eject now emits stop events for module effects in Activation, Target, and Overload categories. Client evidence: `/home/user/eve-logic/eve/client/script/environment/godma.py:1647-1684` refreshes effect wrappers for those categories; `:1441-1467` applies `OnGodmaShipEffect` to `effectsByItemEffect`.
- Deactivate compares the requested effect name with the active `RunningModule.EffectName` using ordinal equality. A mismatch returns `MODULE_NOT_ACTIVE` without issuing a simulation command. Client evidence: `godma.py:2091-2110` passes the name through the client state manager; `:989-998` handles overload separately.
- Whole server source category-filter audit: the Eject path was the only category-based stop-notification filter. Destruction and fit-state paths stop fixed Online effects or rely on simulation state updates; dock, undock, and session transitions contain no other category-based stop-notification filter.

## Further checks

- EVE University describes normal afterburner and MWD cycles as 10 seconds (20 seconds for capital sizes); Afterburner skill shortens duration by 5% per level to 7.5 seconds and reduces its capacitor use by 10% per level. The generic Tech I MWD signature bloom is 500%; variants differ. Eve.rs reads the module and ship values from SDE attributes, and the existing propulsion checks cover the resulting speed, cycle, capacitor, and signature calculations.
- Heat damage, damage HP, rack capacity, attenuation, and thermodynamics values are taken from the SDE profile. The simulation burns the module offline and emits `ModuleEnded` when heat damage reaches structure HP. EVE University documents that burnout behavior.
- EVE University's overheating equation includes an online module-to-slot fraction in addition to rack heat and attenuation. The current Rust chance calculation is `rack heat / capacity * attenuation^distance`, matching EveJS but omitting that occupancy factor. This gameplay difference surfaced during the requested comparison and remains for follow-up; the current work does not change the heat model.
- A burned-out overheated module does not receive an explicit client `OnGodmaShipEffect` overload-stop notification in the observed path: the simulation emits `ModuleEnded`; `SpaceSessions.cs:975-981` clears active/running state and resends the ship. The client uses `godma.py:1441-1467` to update active effect wrappers from `OnGodmaShipEffect`. This path was audited but no notification change was made in this task.
- Client consumer evidence: `godma.py:989-998`, `:1441-1467`, `:1647-1684`, `:2091-2110`; `eve/client/script/environment/shipmodulebutton.py:1287-1294` consumes the ship-effect event.

## Checks

- .NET builds: `Eve.Ships.Tests`, `Eve.Services.Space.Tests`, and `Eve.Host.Tests` succeeded with zero warnings or errors.
- Focused tests: Eject category unit test, Deactivate name matcher unit test, registered DogmaIM Host wire test, and sim heat-burnout test passed (one each).
- `cargo +1.94.1 fmt --all -- --check`, workspace Clippy with warnings denied, and `git diff --check` passed.
- No PostgreSQL database was used. No graphical-client session was run.
