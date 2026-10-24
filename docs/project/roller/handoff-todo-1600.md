# Roller handoff: TODO lines 1600–2000

Scope checked against the current TODO text in this worktree. The existing unrelated TODO edits outside this range and pre-existing source deletions were left untouched.

## Closed rows

- Mission completion atomicity: a failed standing write rolls completion back and leaves the mission accepted; `MissionCompletionTransactionTests.A_standing_write_that_fails_rolls_the_completion_back_and_the_mission_stays_accepted` and `AgentMissionStoreTests.A_completion_rolled_back_with_its_standing_write_leaves_the_mission_accepted_and_announces_nothing`.
- Wormhole connection generator, static connections, and durable lifetime/mass lifecycle jobs: the later consolidated status records seeded generation, expiry, static replacement, transit collapse, and restart in `Eve.Wormholes.Tests`.
- Wormhole effects as dogma modifiers: `SystemEnvironmentTests`; `A_jump_into_a_system_with_an_environment_gives_the_simulation_the_ship_again_with_that_environment`.
- Far-end wormhole body in its own scene and return path: `A_jump_is_taken_by_the_simulation_places_the_destination_end_and_polarizes_the_pilot_so_the_second_try_is_refused`; `An_emptied_connection_collapses_and_both_its_ends_are_taken_out_of_the_simulation`; live `test_wormhole_live.py`.
- Wormhole environment attributes in the Rust simulation after a system change: `A_jump_into_a_system_with_an_environment_gives_the_simulation_the_ship_again_with_that_environment`; `SystemEnvironmentTests`.
- Headless MITM wormhole jump-in and return: `tools/headless-client/tests/test_wormhole_live.py`, recorded as passed in the consolidated wormhole status.

These were stale OPEN entries: later TODO evidence, source code, and named regression tests establish the behavior. Only TODO rows in the assigned range were edited.

## Open rows and evidence/reasons

### Mission execution

- Fetch/courier objectives: `MissionViews`, `MissionComposition`, and `MissionDungeonRuntime` now provide objective payload and turn-in state, with serializer coverage; the full live courier pickup/turn-in walk remains open.
- Dungeon enemy owner and location: the kill objective uses the SDE mission faction and runtime system/coordinates, with the issuer's SDE system as fallback when no runtime position exists. Serialization is tested; saved-position and graphical marker/warp checks remain open.
- Live kill/completion/ISK walk: the recorded live run stops after the first shot with `UnMachoDestination`; completion has unit coverage only.
- Arc 29 live walk: the documented route was not exercised.

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

No PostgreSQL command was used. CI agent-test run `39940268962` passed the focused `Eve.Services.Space.Tests` filter `FullyQualifiedName~A_jump_into_a_system_with_an_environment_gives_the_simulation_the_ship_again_with_that_environment` on pushed commit `3309464f573e587109f5debb6b96770b22309e38`. An earlier dispatch with the abbreviated SHA failed at checkout because Actions treated it as a branch name; the full-SHA retry passed. `ci-status.py failures` found no push-triggered `ci.yml` run for this commit. Other `PgFact` cases cited above were not part of the focused filter. CI agent-test run `39941712144` also passed the `Eve.Host.Tests` filter `FullyQualifiedName~A_standing_write_that_fails_rolls_the_completion_back_and_the_mission_stays_accepted` on commit `8449830e310b9f5b0ac66de6499867f7caa56765`. `ci-status.py failures` found no push-triggered `ci.yml` run for that commit.

## 2026-10-23 mission objective and client walk

- `GetMissionObjectiveInfo` now projects the mission's owner and solar-system location from the mission static catalog and runtime mission state. The issuer's SDE solar system supplies the location when a mission instance position has not been persisted; coordinates are included only when the runtime record contains all three axes. `MissionViewsTests` covers the encoded location and `test_mission_walk.py` now calls the bound RPC after accepting the mission.
- Fetch/courier objective payload and turn-in code are present in `MissionViews`, `MissionComposition`, and `MissionDungeonRuntime`; the section 54 row now distinguishes those tests from the still-held full arc courier walk.
- Local `dotnet build` of `Eve.Host` succeeded (0 warnings/errors), and the focused `MissionViewsTests` case passed (1 passed, 0 skipped). No local PostgreSQL database was used.
- Actions run `40020393673` exercised SKINR, mission, and station live files on main. The Host SKINR dispatch tests passed 4/4. Live tests reported 1 passed, 1 failed, 3 skipped: the SKINR live assertion checked the design name field as if it were the line name. The assertion now checks name field 2 and line name field 3; this correction and the added mission objective call need a fresh run from the pushed commit. SKINR graphical-client verification remains open.

## 2026-10-23 CI follow-up: Host restart signal

- Push CI and focused SKINR runs showed the Host did not close its socket after SIGTERM; an attempted SIGINT was also ineffective because the shell-started background process does not deliver it through `Console.CancelKeyPress`. `Program.cs` now registers POSIX SIGTERM directly and cancels the server token so `HostRuntime.ShutdownAsync` runs; the live test again sends SIGTERM.
- The POSIX SIGTERM handler is pushed as `0649fc34`. Focused run `40026475792` passed Host tests (4/4) and confirmed the Host restarted; `eve-net` then closed with its Host link, so the test could not reconnect through port 26100. Run `40027629354` confirmed a fresh process also rejects the old bearer token (401). The live harness now exports the front binary/listen address and restarts `eve-net` before logging in again, preserving the persisted job/license and notice checks.
- Mission slow-live run `40024793065` passed the basic mission and kill/reward walks, then reached Arc 29 and hit pytest's default 300-second timeout at its 270-second travel wait. All three mission walks now use a 1200-second timeout; a fresh slow-live run will verify Arc 29.
- The current `Eve.Host` build succeeded with 0 warnings and 0 errors; Python compile checks and `git diff --check` passed after the test updates.

## 2026-10-23 verified live follow-up

- Mission slow-live run `40026903022` completed successfully on `623e21803171574c04690d703b8fcb5cf31f2fa3`; the basic, kill/reward, and Arc 29 client walks all completed. The timeout now covers the full 270-second Arc travel step.
- SKINR run `40028889495` on `1bfd22e4e98ee61add508a7e3632c0845bd35e1d` passed `ShipSkinrDispatchTests` (4/4) and applied the persisted license after restart. The assertion failed because it read the headless client's raw packet list without pinging/decoding it.
- `test_skinr_live.py` now uses `notifications_of`, which pings the client, drains packets, and decodes names. Focused run `40030150836` on `1c15eb702dc4ae4ed3c94a6a44cf0cf87f386c2c` passed `Eve.Host.Tests` 4/4 and the SKINR live walk 1/1, with 0 skips.
- Push CI run `40028876361` failed on the old SKINR notice assertion and a concurrent market test expectation. Run `40030137837` passed the SKINR and Rust jobs but failed the unanchor live assertion and two multi-buy market tests; its exact failures are recorded below. A subsequent market commit is under CI run `40031652832`.
- The CI mission artifact confirms the Host build completed with 0 warnings and 0 errors. No PostgreSQL command was run locally; no graphical-client check was performed.

## 2026-10-23 push CI follow-up

- Push run `40030137837` failed `test_a_corporation_unanchors_and_scoops_its_control_tower`: its tower was seeded at `LEGAL_SYSTEM` (`30000903`), but the assertion compared the returned inventory location to the unrelated default `SYSTEM` (`30100032`). `StructureUnanchorDropAuthority` persists the structure record's `SolarSystemId`, matching the actual row and space item. The assertion now checks `LEGAL_SYSTEM`; the focused live check is pending.
- The same run reported `MarketHostTests.The_host_dispatches_accessible_structure_market_orders_and_recovers_partial_settlement_after_restart` and two `Eve.Market.Tests` multi-buy failures. Market changes are being checked by newer CI run `40031652832`; no market code was changed in this task.

## 2026-10-23 control tower scoop custody

- Focused live run `40032438836` passed the Host filter (`5 passed`) and the structures file had `21 passed, 1 failed, 0 skipped`. The unanchor flow correctly produced an item at `LEGAL_SYSTEM`; scoop then failed because `CharacterShips.ScoopSpaceArtifactAsync` compared `SpaceItem.CorporationId` to the item’s character `OwnerId`.
- EveJS v0.12.9 `cargoContainerRuntime.js` (`validateCargoContainerSpaceAccess`, lines 1073–1166) allows a starbase structure when its owner is the pilot or the pilot’s corporation. `scoopSpaceItemToShipHold`, lines 2110–2140, transfers the item to the pilot after validating access. The matching headless flow seeds a character-owned control tower and a corporation-owned space record.
- `SpaceContainers.ScoopAsync` now follows the reference rights for hulls: the pilot may scoop an item they own, or a corporation-owned item when their persisted corporation matches the space record. Fittings keep their separate container loot-access rule. The transaction authority checks SDE group, launcher, and fitting or hull custody before moving the item and deleting the space record. The existing live walk also checks the inventory change, resulting cargo location, and removal of the space row.
- Focused run `40039750142` passed the selected `Bound_ship_scoop_moves_the_fittings_artifact_and_children_then_checks_hull_hold_room` test (1 passed, 0 skipped); the structures file had `21 passed, 1 failed, 0 skipped`. The captured refusal was `NotEnoughCargoSpace` (`available=5040`, `volume=8000`), so the remaining failure was the test ship’s hold, not custody. The push CI run `40039742452` is still running; unrelated market failures from earlier commits have a separate main fix.
- The end-to-end tower walk now uses SDE ship type 20187 (Obelisk), whose cargo hold can carry the 8,000 m³ tower hull. This lets the already-authenticated pilot complete the Scoop call and exercises cargo ownership, `OnItemChange`, and removal of the space row.
- EveJS `validateCargoContainerSpaceAccess` grants a structure scoop to the character owner or the corporation owner. The host applies hull ownership rights before scooping; fittings retain their existing container loot-access rule. The transaction authority checks a fittings owner against its stored character or corporation, while a hull requires the pilot or a corporation-owned hull with matching corporation custody. The live assertion reports server refusal details on failure.
- `SpaceContainersUnanchorDropsTests.Bound_ship_scoop_moves_the_fittings_artifact_and_children_then_checks_hull_hold_room` also covers the distinction: the fittings container remains corporation-owned, and its capacity-check hull is now owned by the pilot so the test reaches the capacity rule as an authorized owner.
- Local builds of `Eve.Host` and the `Eve.Services.Space.Tests` project succeeded with 0 warnings and 0 errors. `python3 -m py_compile tools/headless-client/tests/test_structures.py` and `git diff --check` passed; no local PostgreSQL database was used.
- Focused Actions run `40040879043` passed the selected `Bound_ship_scoop_moves_the_fittings_artifact_and_children_then_checks_hull_hold_room` regression and the `test_structures.py` live file. The `ci-status.py` summary reported job success but did not expose per-file skip totals.
- Push CI run `40040870487` failed outside the tower scoop walk: the structure market and board/eject live walks failed, and Host/other shards reported an RPC inventory count mismatch (expected 1378, got 1382), a contact-notification dictionary cast failure, and the older courier keyword assertion. The exact courier test passed in focused Actions run `40042262043` dispatched with its fully qualified filter against the latest requested main ref. No graphical EVE client check was performed.

