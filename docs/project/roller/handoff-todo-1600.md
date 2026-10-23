# Roller handoff: TODO lines 1600–2000

Scope checked against the current TODO text in this worktree. The existing unrelated TODO edits outside this range and pre-existing source deletions were left untouched.

## Closed rows

- Wormhole connection generator, static connections, and durable lifetime/mass lifecycle jobs: the later consolidated status records seeded generation, expiry, static replacement, transit collapse, and restart in `Eve.Wormholes.Tests`.
- Wormhole effects as dogma modifiers: `SystemEnvironmentTests`; `A_jump_into_a_system_with_an_environment_gives_the_simulation_the_ship_again_with_that_environment`.
- Far-end wormhole body in its own scene and return path: `A_jump_is_taken_by_the_simulation_places_the_destination_end_and_polarizes_the_pilot_so_the_second_try_is_refused`; `An_emptied_connection_collapses_and_both_its_ends_are_taken_out_of_the_simulation`; live `test_wormhole_live.py`.
- Wormhole environment attributes in the Rust simulation after a system change: `A_jump_into_a_system_with_an_environment_gives_the_simulation_the_ship_again_with_that_environment`; `SystemEnvironmentTests`.
- Headless MITM wormhole jump-in and return: `tools/headless-client/tests/test_wormhole_live.py`, recorded as passed in the consolidated wormhole status.

These were stale OPEN entries: later TODO evidence, source code, and named regression tests establish the behavior. Only TODO rows in the assigned range were edited.

## Open rows and evidence/reasons

### Mission execution

- Fetch/courier objectives: the objective payload still has empty lists; the TODO identifies EveJS `buildMissionObjectivePayload` at `agentMissionRuntime.js:5885`, but item-in-cargo/turn-in state is absent.
- Dungeon enemy owner and location: the current TODO records faction shortcut and zero location placeholders.
- Live kill/completion/ISK walk: the recorded live run stops after the first shot with `UnMachoDestination`; completion has unit coverage only.
- Arc 29 live walk: the documented route was not exercised.
- Completion transaction rollback: no test rolls back the completion transaction.

### Zarzakh

- Emanation Lock remains in memory and client notices are not implemented in the public gateway.
- Sovereignty refusal has no claim path in the structures service.
- Bubble/probe restrictions remain a project decision without the source and system rule recorded in this row.
- Client check of embedded flags and a complete lane run remain outstanding.
- Turnur-to-Zarzakh has an eve.rs-only headless pass; no EveJS comparison was recorded and the Cockroach was seeded through the database.

### Pochven and EDENCOM

- Filament entry and Home-system standing: destinations are absent from the client extract (list IDs 4510–4569); Home standing is skipped by owner decision.
- Docking, LP-store, manufacturing, office and delayed-Local standing rules are not implemented.
- Gate sentry project combat has simulation coverage, but source-published retribution timers and graphical-client combat are open.
- Gate defense/skirmish placement uses project population rules; per-site compositions are open.
- Pochven NPC loot/drop tables have no source-backed per-type table.
- Live skirmish/other-gate observation: only 25 seconds were observed against a 900-second cadence; EveJS comparison is also open.
- More EDENCOM fortress/stronghold/minor-victory systems: no dated source names them; the cited wiki list was unavailable.
- Filament destinations and Home standing remain separate open rows in the population section.

### Wormholes and embedded client data

- Class 4 static count: the older row contrasts one EveJS static with the wiki's “usually two”; no verified type-level rule/test was found during this pass.
- J-space Local: request/notice contract and a live two-character walk remain open.
- Shattered Ice Field: present in data but not represented in the site plan.
- Gas/ore live walk and comparison with a stargate jump have no recorded scenario.
- Client attribute refresh after a system change has no graphical-client check.
- Environment trace logging behind an environment variable is not implemented.
- Wormhole ore/gas mining path is not tested.
- Embedded client SDE default-path live run without `EVE_CLIENT_SDE_DIR` remains open.

## Checks in this handoff

No PostgreSQL command was used. CI agent-test run `39940268962` passed the focused `Eve.Services.Space.Tests` filter `FullyQualifiedName~A_jump_into_a_system_with_an_environment_gives_the_simulation_the_ship_again_with_that_environment` on pushed commit `3309464f573e587109f5debb6b96770b22309e38`. An earlier dispatch with the abbreviated SHA failed at checkout because Actions treated it as a branch name; the full-SHA retry passed. `ci-status.py failures` found no push-triggered `ci.yml` run for this commit. Other `PgFact` cases cited above were not part of the focused filter.
