# ST-E2 handoff: Mobile Observatory and the custom decloak notice (2026-10-15)

Done: the Mobile Observatory deployable (type 58904, group 4107, category 22) and its remote decloak ping. It launches from the hold, is anchored ten minutes after the drop,
pings its system at anchoring and every ten minutes after, and leaves with its item at its lifetime (sweep, `space.containers.sweep`). The ping is the only caller
of `DecloakCause::Custom`, so `OnCloakModuleForceDeactivated` with reason `mobileObservatory` reaches the pilot. Protocol 71 to 72 (kinds 90 and 91).

Files: crates/eve-space/src/scene/decloak.rs (`remote_decloak_ping`, constants); deployable.rs (`Role::MobileObservatory`, `place_observatory`);
link/mod.rs, link/kind.rs, link/payload.rs, link/decode_message.rs (wire); sim/dispatch.rs; scene/mod.rs (`Pilot.cloak_stabilized_until`); modules/effects.rs (stamp on cloak
start); modules/tests.rs (3 sim tests); sim/tests.rs (protocol pin 72); tests/simlink_fixtures.rs; tools/dev/gen-simlink-fixtures.py and oracle/fixtures/simlink/messages.json;
dotnet/src/Eve.Space/ (SimCodec, SimMessages, ISimulation, SimClient); dotnet/src/Eve.Services.Space/SpaceContainers.Disruptors.cs (launch, anchor, ping job,
spacing), SpaceContainers.Deployed.cs (restore), SpaceContainers.cs (expiry); dotnet/src/Eve.Host/Composition/SpaceComposition.cs (ping job registration);
dotnet/src/Eve.Ships/Migrations/0014_mobile_observatory_kind.sql (deployed kind); dotnet/tests/Eve.Services.Space.Tests/MobileObservatoryHostTests.cs and Rig.cs;
dotnet/tests/Eve.Space.Tests/SimCodecTests.cs and FakeSim.cs.

Tests run (eve_p4 under db-lock.sh; counts from the logs):
- Rust `cargo test -p eve-space -p eve-net`: eve-space lib 834 passed, 0 failed (before the final narrow run); eve-net and the integration suites pass. `cargo check --workspace --tests` passes.
- .NET `Eve.Space.Tests`: 350 passed (full), SimCodec filter 333 after the merge.
- .NET `Eve.Ships.Tests`: 229 passed (full, covers migration 0014).
- .NET `Eve.Services.Space.Tests`: MobileObservatoryHostTests 6 + InhibitorHostTests 4 passed (10/10) after the merge. The full suite is run once at the end (see the push report).

Gaps, not done:
- Deployment rules still missing: the wiki's 100 km from a player control tower; EveJS distances from stations, stargates and Upwell structures (constant unverified); Abyssal refusal.
- The observatory's slim item does not carry component_activate or the decloak emitter attributes (EveJS `hydrateMobileObservatoryEntityFromInventoryItem`); the client ball and its FX are MITM only.
- The cloak stabilization is 15 minutes for every cloak (EveJS reads the module's stabilizeCloakDuration). Covert cloaks are pinged (see DEVIATIONS, ST-E2).
- The Host test rig registers the ping job by hand (Rig.cs), like the anchor job; SpaceComposition's registration line compiles but no composition-level test covers it.
- The mobile observatory's ping is per system: an unloaded system pings nothing (EveJS also skips an unloaded scene).

Gotchas:
- The `deployed` kind CHECK constraint is replaced by migration 0014; any new deployed kind needs the same.
- The scheduler's upsert only merges into pending rows, so the ping job reschedules itself from inside its handler.
- Run Services.Space tests with EVE_PG_URL=Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p4 under tools/dev/db-lock.sh.
