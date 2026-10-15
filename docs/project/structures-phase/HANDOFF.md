# Structures phase: master handoff

Written for a different Claude (or person) continuing the work. Plan: `plan.md` (work packages ST-0..ST-11, ST-C, ST-E, rules adopted,
wiki/EveJS/SDE evidence). Rules for implementers: `agent-rules.md` (read before touching anything). Per-package state: `handoff-ST-*.md`
in this folder. Open rows: `docs/project/TODO.md` (sections 44-48). Conflicts and decisions: `docs/reference/compat/DEVIATIONS.md`.

Current source priority: gameplay rules and numbers use CCP official documentation and the matching SDE first, with EVE University wiki as a secondary reference. Client wire shapes use V24.01 eve-logic callsites and real client traces; EveJS v0.12.9 is the executable functional reference. Record justified differences in DEVIATIONS.

## Status at hand-off (main, sim PROTOCOL_VERSION 62)

| Package | State | Notes |
|---|---|---|
| ST-0 structure record owner, state machine, timers | done | `Eve.Structures`; GameStore `structures` imported once and frozen |
| ST-1 structure ball in the sim | code complete | kinds 74-77, protocol 59/60; headless `test_structures.py` never ran live |
| ST-2 deployment / vulnerability RPCs | partly | rename, reinforce times, Anchor, Unanchor/Cancel, 3 notices done; unanchor-completion drop, some Anchor refusals, reinforcement-change mail are open (see its handoff) |
| ST-3 structure docking / hangars | PG verified | Dock, Undock, hangar listing, guest lifecycle, OnHangarViewStateUpdated and SDE/cargo confirmation gate pass PG tests. Customs fines/confiscation are a separate flow; graphical and live headless acceptance remain open (see its handoff) |
| ST-4 tether | PG and Rust verified | protocol 62; leased core/ACL/FW permissions, immunity, engage-only repair, cyno/ship/disconnect lifecycle, FX replay and fleet-compression refusal. Rust 773 and Space PG 735 checks pass; native graphical/grid acceptance remains open. |
| ST-5 control / boarding / deliveries | PG and Rust verified | protocol 65 observer, safe takeover/release, structure inventory/dogma reads, same-transaction delivery inbox, authoritative cargo range; native acceptance and ST-8 execution remain open. |
| ST-6a moon extraction (host) | PG verified | effective fitted SDE rig rules, start-time yield/deadline/lifetime snapshots, actor lifecycle events and durable types 202-206 owner inbox are wired; native acceptance open. |
| ST-6b moon fields / ledger | PG and Rust verified | persisted multi-ore fields, restart/depletion, refinery observer ledger and durable field removal; native graphical acceptance open. |
| ST-7 structure services | partly | fuel, compression plant, cyno jammer E-03/E-15 (jammer gated by the Cynosural Suppression upgrade), structure beacon (checks, placement, jump), fuel owner notices, Host tests done; corporation-office compression source open (needs the corporation hangar take-access model, see handoff-ST-7-closure.md) |
| ST-10 hacking | closed with 2 open rows | site completion on emptying and removal of an emptied ball need an `inventory.item_moved` event |
| ST-11 cosmic sites | closed | NPC waves, containers via ST-10 |
| ST-C in-space compression | done | ship module, not a structure |
| ST-E (E-01, E-04, E-02/E-05 assertions) | done | custom cloak notice needs Mobile Observatory |

ST-5 core control/boarding/deliveries is implemented and verified by PG/Rust regression tests (protocol 65); retained-hull destruction now persists a replacement capsule and supports failed-transaction/release retries. Native graphical acceptance remains open. ST-6a rig rules/owner notifications and ST-6b moon ore fields/ledger/decay are now wired and regression-tested. Next: ST-8a/8b structure modules and doomsdays, ST-9 entosis
(depends on a Sovereignty Hub ball). Order and dependencies: `plan.md` section 5.

## ST-5 implementation (2026-10-12)

- `StructurePilotStore` owns exclusive controller records and sequence-backed takeover generations. Host startup clears stale controllers. Failed simulation transitions compensate SQL ownership; undock/disconnect and profile/state changes release control.
- Protocol 65 / kind 84 (wormhole placement/removal retain 82/83) (`ControlStructure`, command 33) attaches a real structure observer. The original physical ship remains in the scene; Board/Eject session changes, capsule creation, bind/updateState and visibility share existing owners. Command 19 reads `StructureHealth`, not the retained ship. `OnShipCosmeticsChanged` follows successful session changes to clear the former/restored ship's client fitting cache, matching the V24.01 consumer.
- Inventory/dogma initialization projects actual structure items, SDE attributes and module online state. Every read checks the current controller and profile setting 17; structure inventory mutations are explicitly refused. Personal active-ship persistence is retained.
- Deliver moves the owned hangar subset and stores type 199 / group 4 `StructureItemsDelivered` in the existing mailbox within the same inventory/outbox transaction. The existing mail mutation gate protects cache publication; retry creates no duplicate notification. Commit precedes `OnItemChange` and inbox publication.
- Cargo Deposit uses Measure 20 `[structureId, shipId]`: real scene surface distance, cloak/warp/observer refusal, plus Host weapon/criminal checks. The 10 km gate grants no docking rights; dockable deliveries remain personal, FLEX fuel is restricted by SDE and belongs to the structure corporation.
- Retained-hull loss is connected through existing unpiloted tracking: loss/cargo/wreck and replacement capsule/active ID commit atomically; the controller remains attached. Ordinary abandoned-ship calls retain the active-ship refusal. Failed persistence is retried on release, and real Rust preserves the respawn context through detach/control restoration. Space controller 7 and Ships death 13 PG tests pass.
- Known open work: structure capacitor/module execution and fitted hardpoint accounting are ST-8; capacitor currently stays full in the existing simulation model. ST-6a mutation control/profile gate, owner-corporation notices and rig modifiers are now wired and tested; see ST-6 evidence below. Graphical client/HUD acceptance and a live Host-to-sim walkthrough were not run.
- Functional sources: pinned local EveJS `828823a75f211fdecb945b087a26c225f111491f`, V24.01 eve-logic `1dbb5711a072a146e1d228365668ac05655eefe1`, CCP Upwell Quick Facts / Roles Listing / Cargo Deposit announcement. EveJS `structureControlService.js` was executed with isolated dependencies (11 wrapper checks); this does not establish full scene parity.
- Before upstream integration: Rust 783 (753 unit + 30 integration), Host structure 41, Dogma 133, Inventory 46, and Space controller 5 passed. After upstream merge `97126166`, `cargo test -p eve-space -p eve-net -- --quiet` passed 815 checks (space 784, net 31), with no failure or ignored test. Full .NET suites passed: Host 259, Core 669, codec 310, Structures 93, Dogma 133, Inventory 46, Space 809 (no failure/skip). The final cosmetics-order adjustment is covered by a separate rerun of the 5 Space controller and 12 Host controller/read-model tests. `cargo build -p eve-sim -p eve-net` also succeeded. A subsequent upstream merge of `32176bd7` keeps wormhole kinds 82/83 and moves control to 84, negotiating protocol 64 on both sides. Regenerated goldens contain 187 valid / 65 invalid frames; Rust rechecks passed 823 (space 792, net 31), codec 312. After this merge Host structure 41 and full Space 809 passed again (0 failure/skip). Retained-hull gates subsequently passed: Space control 7, Ships death 13; Rust Space 794 (764 unit + 30 integration). The real Sim regression includes a control restoration/retry before capsule respawn. Latest upstream `207e7670` was merged as `c024ab13`; the combined wire is protocol 65 (upstream adds Destroyed type/corporation fields). Rust Space/net 841, codec 313, and Space control/NPC bounty 15 passed; the final Host control/delivery/moon/registered-delivery subset passed 43 (no failure/skip). `cargo build -p eve-sim -p eve-net` succeeded again. Mission metadata was loaded into `sde_mission` (build 3405148, 2892 missions + 21 epic arcs); primary `sde` remains client build 3396210.

## ST-6 implementation (2026-10-12)

- Host wires MoonRigReadModel into MoonExtractions, SpaceMoonFields into the shared EventBus, and MoonOwnerNotifications after MailSend has initialized its shared mutation gate. Actor IDs come from the RPC session. Notifications use the EventContext transaction and publish cache/live state after commit.
- Persisted rock metadata and extraction lifetime snapshots use additive migrations 0013_moon_field_metadata (Ships) and 0006_moon_field_decay_snapshot (Structures). Existing rows retain legacy -1 lifetime fallback. Depleted quantities remain zero across restart, and normal container expiry defers moon rocks to the durable field job.
- Focused evidence: moon Host/rig tests 20 PASS, owner inbox tests 3 PASS, field/mining/restart/decay tests 7 PASS; final combined moon/work-cycle/mining tests 19 PASS; full Structures tests 98 PASS, all without skips. Full Host tests 276 PASS and Rust eve-space 812 PASS (782 unit + 30 integration), all without skips; Full .NET Space tests 829 PASS before the final expired-moon mining guard; the final focused run includes that guard, volume conservation and field-radius rig validation. Full Ships tests 229 PASS; mining ledger service tests 7 PASS. Final Host moon/composition tests 36 PASS; cargo build -p eve-sim succeeded. All reported runs have zero failures/skips. Latest Codebase Memory refresh: 53,095 nodes / 318,678 edges, parse_unusable=0; partial SQL/doc parser coverage is not a completion claim.
- Deliberate SDE-based difference: stability rigs apply functional decay multiplier 2706 once; display attribute 2728 is not multiplied after its rig modifier. Medium/large stability rigs yield 72 h, not EveJS's doubled 108 h.
- Cancel now requires the owning corporation and Director/Station Manager rights; existing corporation session rules expand CEO privileges. CCP Moon Mining is the authority for the owner-only cancel rule, which is stricter than EveJS's unchecked session role. The 12 moon Host tests and 17 Event Bus tests pass before feature integration.
- EventContext supports AfterCommit publication and AfterCompletion cleanup, so mailbox gates and physical placement need not publish uncommitted state. Post-commit failures are logged without undoing durable state; required retry belongs in an outbox event/job.
- Delivery: implementation commit 060c1c37; upstream through e7e3422d merged as 94a45171 without conflicts. Post-merge Host moon tests 26 PASS; post-merge field tests 7 PASS, zero failure/skip. All three implementation workers were closed and archived after handoff. Subsequent wormhole upstream 5cc6a133/70e4c252 merged as 492548cb; post-merge full Rust eve-space tests 814 PASS (784 unit + 30 integration), zero failures/ignored tests. Push of 492548cb succeeded and remote HEAD equality/clean main were confirmed.
- Rules/static data remain official build 3396210 (attributes 2698, 2704, 2707 and related lifespan/rig fields). Sources: https://support.eveonline.com/hc/en-us/articles/115005404229-Moon-Mining and https://support.eveonline.com/hc/en-us/articles/115005402265-Mining-Ledger, pinned local EveJS/client references from ST-5.

## Things that will bite

- `EVE_PG_URL` form: the suite accepts both `postgres://...` and key=value (`Host=127.0.0.1;Username=postgres;Database=eve`); use key=value for dotnet.
- Heavy lock: `flock -o -w 1800 /tmp/eve-heavy.lock <cmd>` and `-p:UseSharedCompilation=false` on every dotnet build/test. Without `-o` the
  compiler server inherits the lock fd and holds it forever (this stalled the queue twice).
- `Eve.Services.Space.Tests` takes 8-10 minutes. One unexplained intermittent failure was seen once on the last runs (name not captured); run
  with `--logger "trx;LogFileName=..."` to catch it.
- `MissionSdeDataTests` need the `sde_mission` schema; they fail on a database without it (environmental).
- Origin moves every few minutes: after your tests pass, merge, check that origin's changed files do not overlap yours, run only the touched
  builds and your own filters, and push at once. A full rerun after every rebase never converges.
- Current Windows work uses `C:\Users\yutai\eve.rs` on `main`. Use Codebase Memory for source discovery and Serena for source inspection/edits; both MCPs are mandatory. Keep shared subagent ownership disjoint and serialize .NET builds/tests.
- Never edit an existing README.md. New docs go to new files, TODO.md, DEVIATIONS.md or an ADR.
- Commit identity `Fokiiiiiii <229531129+Fokiiiiiii@users.noreply.github.com>`, English messages, no attribution trailers. Push to `main`, no force, no PR.
- Real-client verification (MITM) is not done for any structure feature. Slim rendering, deployment UI, docking visuals, tether FX, hacking window,
  signature id format and moon FX are only provable with the graphical client.

## ST-8a skill-input preparation (2026-10-13)
StructureControlReadModel now adds only the four CCP-defined structure-specific skills to its DogmaWorld and copies their mutable item state. Personal skill metadata and total SP remain intact for the existing shipState wire projection. No module activation or Sim wire change is included in this slice.

Evidence: the corrected regression failed on the original shared-item loop; the fixed Host StructureControl subset passed 12 tests (0 failure/skip). Upstream 6119b18d is integrated; post-merge Inventory 63 and Rust simlink fixtures 8 passed. See handoff-ST-8a-skills.md and continuation-20261013.md. Next remains ST-8a ordinary module execution, then ST-8b. EveJS end-to-end and native client acceptance are open.
