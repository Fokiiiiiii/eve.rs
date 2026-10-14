# ST-8a Simulation Handoff

## Status

The Rust scene/module slice is implemented and focused tests pass. Overall end-to-end completion still depends on the Host-side authoritative damage-target resolver and client-level verification.

## Runtime contract

- Kind 85/86 are generic per-damage-step authorization messages. Rust uses the exact correlation tuple: session, controlled structure, module, effect, activation nonce, Host control-lease generation, cycle, and zero-based pulse/hop ordinal. Returned IDs must be a unique subset of the pending request and are revalidated against the live scene before application.
- ControlStructure.control_lease_generation is carried from the Host StructurePilotRecord.Version; Sim does not generate a substitute lease.
- Point Defense scans every current local, in-range damageable dynamic entity, then waits for Host authorization before committing charge or damage. It does not filter the request to player ships.
- Controlled-structure AoE pulses and Vorton links request fresh candidate IDs for each pulse/hop. Charge is committed after the first matching grant; later links do not spend another round. Stale, mismatched, duplicate, released-lease, and out-of-request replies cannot authorize damage.
- Structure Vorton uses weapons::Arc.damage_loss_target: Option<f64>. With the SDE 3396210 value on Standup Vorton type 35928 (effect 6447; attrs 2104=5, 2105=20000, 2106=0.85), each authorized secondary hop applies damage_loss_target.powf(hop_ordinal + 1) to the four-channel damage vector. The primary shot is unchanged. None leaves damage unchanged, matching ship Vortons without attr 2106. Action::Doomsday remains unchanged; type 40634 has no lightning target-count/range attrs.
- Sim-owned NPC and missile identities are published after insertion/update and removed on non-death teardown using Kind 88/89. Death remains handled by SimDestroyed. Cyno beacon removal is scoped by solar system, exact beacon ID, and beacon role (Kind 87). SDE group 306 is recognized as a container without changing its group/type identity.

## Verification

Fresh focused Rust results:

- modules::tests::a_controlled_structure_vorton_arc_rechecks_and_authorizes_each_hop — 1 passed. This checks fresh candidates, charge timing, and a 0.85 second-hop damage ratio.
- weapons::tests::a_structure_vorton_arc_applies_attr_2106_decay_per_secondary_hop — 1 passed. Checks 0.85 then 0.85² and the no-attr None path.
- modules::tests::a_controlled_structure_point_defense_waits_for_the_correlated_authorization — 1 passed.
- modules::tests::a_controlled_structure_strike_authorizes_each_pulse_with_fresh_candidates — 1 passed.
- weapons::tests::a_vorton_arc_strikes_the_ships_near_its_target_nearest_first_and_not_the_shooter_or_the_far_ones — 1 passed.
- Serena error diagnostics for the four edited Rust files returned empty results. Isolated rustfmt --check for weapons/mod.rs and weapons/tests.rs passed. A broader check of the current module files reported additional formatting diffs; no whole-file formatter rewrite was applied.
- Wire-owner report (not rerun by this worker): fixture generator output is 196 valid / 81 invalid. No .NET build/tests or graphical-client run was performed.

## Remaining integration

- Host must resolve every candidate through authoritative live player/ejected-hull, launched drone/fighter, NPC profile, and structure records; validate SDE damageable type and coherent owner/corporation/alliance/faction identity. Candidate metadata is not authorization evidence. Rust-local Pochven NPC profiles are emitted, but Host profile registration/resolution remains Host-owned.
- Action::Field on controlled structures remains fail-closed because an allowed-ID set cannot distinguish moved from held targets. A source-backed disposition contract is still required.
- Full Host resolver behavior, restart/reconnect handling across services, and the unmodified V24.01 graphical client were not verified here.
