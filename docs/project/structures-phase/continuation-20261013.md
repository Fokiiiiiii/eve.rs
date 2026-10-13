# Structures continuation: ST-8a (2026-10-13)

## Objective and current boundary
Continue `structures-phase/HANDOFF.md` from ST-5/ST-6 into ST-8a: a controlling pilot must activate and deactivate an ordinary fitted Standup launcher or electronic warfare module on the actual structure. Use authoritative inventory, controller lease, profile permission, dogma values, charge state, target/range checks and simulation results. Preserve the pilot's retained personal ship.

Source snapshot before this increment: canonical `C:/Users/yutai/eve.rs`, `main`, upstream `9db7d354`. Earlier response-audit source changes are present and owned separately; preserve and verify them before integration. Historical Linux/worktree/source-priority instructions do not override current AGENTS/user rules.

## Ownership and sequence
1. Parent: existing shared-tree baseline build, regression checks, official-source/SDE evidence, interface agreement, final diff review, Git delivery and this record.
2. Luna Host: controlled-structure module snapshot and activation/deactivation, existing authority and charge/notification wiring, focused C# tests. No Rust or shared wire schema edits.
3. Luna Sim: structure source identity, target/range/capacitor and cycle execution, release/disconnect cleanup, focused Rust tests. No C# or shared wire schema edits.
4. Parent: integrate agreed contract, run regression and Host-to-Sim scenarios, update HANDOFF/TODO with exact evidence and push each verified increment.

Keep SimEffect and existing module commands if they express the contract. If a wire extension is necessary, the parent owns the protocol/codec/messages/generator/fixtures together. Serialize .NET builds/tests. Actors use the same canonical checkout and gpt-6-luna/max, with no nested workers.

## Acceptance cases
- Current controller, allowed profile and valid fitted online structure module: correct structure source, SDE/dogma values, accepted cycle and ordered client effects.
- Wrong/non-controller, expired/released control, offline module, invalid/immune target, missing charge, range, insufficient capacitor, cancellation and retry: no false success or uncommitted consumption.
- Repeated cycles consume the actual charge/capacitor once; deactivation, release, disconnect or destroyed/disabled structure stop modules and clear target state.
- The retained hull, active personal ship persistence, normal ship module behavior and other sessions remain consistent.
- PG/codec/unit/headless/native evidence is recorded separately. Native client acceptance is still open.

## Deferred package boundary
ST-8b doomsdays, HOG, burst projectors and chain lightning follow the ordinary ST-8a execution path. ST-9 entosis has a separate sovereignty owner dependency. Their routes are not declared complete by this increment.

## Evidence checked by the parent
- CCP Upwell Structures Quick Facts: https://support.eveonline.com/hc/en-us/articles/209985225-Upwell-Structures-Quick-Facts (retrieved 2026-10-13). Direct pilot control is required for defensive modules; structure safety is permanently Green and the controller gets no suspect/criminal flags. Normal implants, wormhole effects and ordinary skills do not modify the structure; only the named structure-specific skills do. Modules need no skill prerequisite to operate.
- DBHub confirmed the regression connection is `eve_rs_test`, PostgreSQL 18.6. Matching SDE tables expose the published Astrahus 35832 (group 1657), Standup Multirole Missile Launcher I 35922 (group 1562, default effect 101), and Standup Heavy Energy Neutralizer I 35925 (group 1329, default effect 6216).
- `sde.typeDogma` and `sde.dogmaAttributes` name/value cross-check: Astrahus capacitorCapacity 482 = 200000 and rechargeRate 55 = 3600000 ms. Attribute 479 is shieldRechargeRate and must not be used for capacitor recharge. Launcher speed 51 = 3000 ms and reloadTime 1795 = 10000 ms. Heavy neutralizer capacitorNeed 6 = 1000, duration 73 = 15000 ms, maxRange 54 = 100000 m, energyNeutralizerAmount 97 = 1500, falloffEffectiveness 2044 = 100000 m. Runtime must read/derive these from the existing SDE/dogma owner; these evidence values are not runtime constants.
- Parent baseline Host Debug build passed with 0 warnings and 0 errors after upstream 9db7d354 integration. Newly added tests from prior audit work have fixture/analyzer compile failures; those are assigned back to their existing owners and are not reported as passes.

## Agreed existing-wire contract (2026-10-13)
The Host and Sim workers independently confirmed that no new message is required for this slice. Existing ShipSpec carries ship identity, capacitor_capacity in GJ, capacitor_recharge_s in seconds, condition, targeting data and module specs. Host publishes ShipChanged on the controller session with ship_id equal to the controlled structure before Activate. Convert SDE rechargeRate milliseconds to seconds once on Host. Sim resolves observer Activate/Deactivate/ShipChanged against the structure, emits ChargeSpent/ModuleEnded/Notify to the controller session and preserves the retained hull. Structure capacitor belongs to the structure and is not refilled on release/recontrol.

Current missing paths confirmed by the Sim worker: observer ShipChanged is ignored and observer Activate returns NO_SHIP_IN_SPACE. Host currently returns no activation snapshot for a structure observer. These are implementation targets, not completed behavior.

Parent baseline: Eve.Structures.Tests Debug 100 PASS, 0 FAIL, 0 SKIP; Host selected structure/moon/industry/PaperDoll/SKIN tests 103 PASS, 0 FAIL, 0 SKIP. The baseline predates the new ST-8a implementation and cannot establish its acceptance.

## Delivered increment and cost scope (2026-10-13)
The active increment was narrowed to structure Dogma skill inputs and independent mutable skill item state. The earlier Host/Sim activation contract is a future plan, not delivered module execution. Only one newly spawned gpt-6-luna agent was used for this correction; both its configuration-check and implementation turns were verified as gpt-6-luna/max in runtime metadata. It was closed after handoff. Do not assume an old agent's model remains unchanged when resuming it.

Completed evidence: original shared-item loop fails the corrected PG regression; fixed StructureControl tests 12 PASS; post-merge Inventory tests 63 PASS; Rust simlink fixtures 8 PASS; isolated EveJS allowlist helper comparison 3 PASS. No native client/server walkthrough is claimed. The retained hull, full character skill metadata/total SP and existing controller/profile gates are preserved.
