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
| ST-5 control / boarding / deliveries | PG/Rust core verified; retained-hull loss follow-up in progress | protocol 64 observer, safe takeover/release, structure inventory/dogma reads, same-transaction delivery inbox, authoritative cargo range; native acceptance and ST-8 execution remain open. |
| ST-6a moon extraction (host) | partly | notices to owner corp characters and rig modifiers (2704, 2707) open; moon mutation control/profile gate now uses the pilot store and transactional row locks (10 PG Host tests); notices/rig modifiers remain open. |
| ST-7 structure services | partly | fuel, compression plant, cyno jammer E-03/E-15 done; cyno beacon jump open |
| ST-10 hacking | closed with 2 open rows | site completion on emptying and removal of an emptied ball need an `inventory.item_moved` event |
| ST-11 cosmic sites | closed | NPC waves, containers via ST-10 |
| ST-C in-space compression | done | ship module, not a structure |
| ST-E (E-01, E-04, E-02/E-05 assertions) | done | custom cloak notice needs Mobile Observatory |

ST-5 core control/boarding/deliveries is implemented and verified by PG/Rust regression tests (protocol 64); retained physical-hull destruction while controlling is being connected to Host persistence. Native graphical acceptance remains open. Not started: ST-6b moon rocks in space, ST-8a/8b structure modules and doomsdays, ST-9 entosis
(depends on a Sovereignty Hub ball). Order and dependencies: `plan.md` section 5.

## ST-5 implementation (2026-10-12)

- `StructurePilotStore` owns exclusive controller records and sequence-backed takeover generations. Host startup clears stale controllers. Failed simulation transitions compensate SQL ownership; undock/disconnect and profile/state changes release control.
- Protocol 64 / kind 84 (wormhole placement/removal retain 82/83) (`ControlStructure`, command 33) attaches a real structure observer. The original physical ship remains in the scene; Board/Eject session changes, capsule creation, bind/updateState and visibility share existing owners. Command 19 reads `StructureHealth`, not the retained ship. `OnShipCosmeticsChanged` follows successful session changes to clear the former/restored ship's client fitting cache, matching the V24.01 consumer.
- Inventory/dogma initialization projects actual structure items, SDE attributes and module online state. Every read checks the current controller and profile setting 17; structure inventory mutations are explicitly refused. Personal active-ship persistence is retained.
- Deliver moves the owned hangar subset and stores type 199 / group 4 `StructureItemsDelivered` in the existing mailbox within the same inventory/outbox transaction. The existing mail mutation gate protects cache publication; retry creates no duplicate notification. Commit precedes `OnItemChange` and inbox publication.
- Cargo Deposit uses Measure 20 `[structureId, shipId]`: real scene surface distance, cloak/warp/observer refusal, plus Host weapon/criminal checks. The 10 km gate grants no docking rights; dockable deliveries remain personal, FLEX fuel is restricted by SDE and belongs to the structure corporation.
- Follow-up in progress: a retained ship destroyed under its simulation-only session currently bypasses Host death persistence while the controller remains attached. The existing abandoned-ship mutation rejects the persistent active ship, so this needs a shared lifecycle transition rather than only adding an `_ejectedShips` entry.
- Known open work: structure capacitor/module execution and fitted hardpoint accounting are ST-8; capacitor currently stays full in the existing simulation model. ST-6a mutation control/profile gate is now wired and tested; owner-corporation notices and rig modifiers remain open. Graphical client/HUD acceptance and a live Host-to-sim walkthrough were not run.
- Functional sources: pinned local EveJS `828823a75f211fdecb945b087a26c225f111491f`, V24.01 eve-logic `1dbb5711a072a146e1d228365668ac05655eefe1`, CCP Upwell Quick Facts / Roles Listing / Cargo Deposit announcement. EveJS `structureControlService.js` was executed with isolated dependencies (11 wrapper checks); this does not establish full scene parity.
- Before upstream integration: Rust 783 (753 unit + 30 integration), Host structure 41, Dogma 133, Inventory 46, and Space controller 5 passed. After upstream merge `97126166`, `cargo test -p eve-space -p eve-net -- --quiet` passed 815 checks (space 784, net 31), with no failure or ignored test. Full .NET suites passed: Host 259, Core 669, codec 310, Structures 93, Dogma 133, Inventory 46, Space 809 (no failure/skip). The final cosmetics-order adjustment is covered by a separate rerun of the 5 Space controller and 12 Host controller/read-model tests. `cargo build -p eve-sim -p eve-net` also succeeded. A subsequent upstream merge of `32176bd7` keeps wormhole kinds 82/83 and moves control to 84, negotiating protocol 64 on both sides. Regenerated goldens contain 187 valid / 65 invalid frames; Rust rechecks passed 823 (space 792, net 31), codec 312. After this merge Host structure 41 and full Space 809 passed again (0 failure/skip). A retained-hull destruction follow-up is under implementation and will need its own fresh gates. Mission metadata was loaded into `sde_mission` (build 3405148, 2892 missions + 21 epic arcs); primary `sde` remains client build 3396210.

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
