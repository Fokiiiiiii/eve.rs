# Structures phase: master handoff

Written for a different Claude (or person) continuing the work. Plan: `plan.md` (work packages ST-0..ST-11, ST-C, ST-E, rules adopted,
wiki/EveJS/SDE evidence). Rules for implementers: `agent-rules.md` (read before touching anything). Per-package state: `handoff-ST-*.md`
in this folder. Open rows: `docs/project/TODO.md` (sections 44-48). Conflicts and decisions: `docs/reference/compat/DEVIATIONS.md`.

Source priority (user rule): stats/numbers = SDE; mechanics = EVE University wiki (implement as written), then EveJS / CCP / eve-logic
at equal rank; client wire shapes = eve-logic. When no source states the spec, the wiki decides. Record every conflict in DEVIATIONS.

## Status at hand-off (main, sim PROTOCOL_VERSION 60)

| Package | State | Notes |
|---|---|---|
| ST-0 structure record owner, state machine, timers | done | `Eve.Structures`; GameStore `structures` imported once and frozen |
| ST-1 structure ball in the sim | code complete | kinds 74-77, protocol 59/60; headless `test_structures.py` never ran live |
| ST-2 deployment / vulnerability RPCs | partly | rename, reinforce times, Anchor, Unanchor/Cancel, 3 notices done; unanchor-completion drop, some Anchor refusals, reinforcement-change mail are open (see its handoff) |
| ST-3 structure docking / hangars | partly | Dock, Undock and hangar listing done (PG tests); OnHangarViewStateUpdated, contraband and structure guest notices open; headless scenario never ran live (see its handoff) |
| ST-6a moon extraction (host) | done | notices to owner corp characters and rig modifiers (2704, 2707) open; control gate is provisional until ST-5 |
| ST-7 structure services | partly | fuel, compression plant, cyno jammer E-03/E-15 done; cyno beacon jump open |
| ST-10 hacking | closed with 2 open rows | site completion on emptying and removal of an emptied ball need an `inventory.item_moved` event |
| ST-11 cosmic sites | closed | NPC waves, containers via ST-10 |
| ST-C in-space compression | done | ship module, not a structure |
| ST-E (E-01, E-04, E-02/E-05 assertions) | done | custom cloak notice needs Mobile Observatory |

Not started: ST-4 tether, ST-5 control/boarding/deliveries, ST-6b moon rocks in space, ST-8a/8b structure modules and doomsdays, ST-9 entosis
(depends on a Sovereignty Hub ball). Order and dependencies: `plan.md` section 5.

## Things that will bite

- `EVE_PG_URL` form: the suite accepts both `postgres://...` and key=value (`Host=127.0.0.1;Username=postgres;Database=eve`); use key=value for dotnet.
- Heavy lock: `flock -o -w 1800 /tmp/eve-heavy.lock <cmd>` and `-p:UseSharedCompilation=false` on every dotnet build/test. Without `-o` the
  compiler server inherits the lock fd and holds it forever (this stalled the queue twice).
- `Eve.Services.Space.Tests` takes 8-10 minutes. One unexplained intermittent failure was seen once on the last runs (name not captured); run
  with `--logger "trx;LogFileName=..."` to catch it.
- `MissionSdeDataTests` need the `sde_mission` schema; they fail on a database without it (environmental).
- Origin moves every few minutes: after your tests pass, merge, check that origin's changed files do not overlap yours, run only the touched
  builds and your own filters, and push at once. A full rerun after every rebase never converges.
- Serena resolves to the main checkout, not a worktree: edit with python3 scripts and check `git status` after every edit.
- Never edit an existing README.md. New docs go to new files, TODO.md, DEVIATIONS.md or an ADR.
- Commit identity `Fokiiiiiii <229531129+Fokiiiiiii@users.noreply.github.com>`, English messages, no attribution trailers. Push to `main`, no force, no PR.
- Real-client verification (MITM) is not done for any structure feature. Slim rendering, deployment UI, docking visuals, tether FX, hacking window,
  signature id format and moon FX are only provable with the graphical client.
