# Roller handoff: TODO range 1200–1600

Scope: the rows that were in `docs/project/TODO.md` lines 1200–1600 at the origin/main snapshot used for this run. The worktree started behind origin/main and had nine unrelated tracked deletions; those files were left untouched. TODO and DEVIATIONS had no incoming changes at the fetched origin/main snapshot.

## Closed or reconciled

- 1240 — Data/relic site containers, hacking, loot and clear event. Evidence: `SiteContainerHacks.OnItemLeftAsync`; `SiteContainerTests`, `SiteLootTests`, `ContainerSiteEndToEndTests`, `SiteContainerRestartTests`, `HackingCompositionTests`; section 48 supersedes the old ST-10 blocker.
- 1259 — Epic arc inventory reconciled to 21 in section 43b; the wiki/design lists were incomplete subsets. Evidence: section 43b inventory and owner-confirmed decision in DEVIATIONS “Epic arc availability”; `EpicArcOffersTests` covers arc offer behavior, not the total count.
- 1368 — Corporation-office compression source and access path. Evidence: `StructureCompressionAuthority.SourceItemAsync`; `StructureCompressionTests` (9). Live walk remains open as a separate row.
- 1372–1373 — Pharolux jump-drive and Host registry backend. Evidence: `StructureBeaconTravelTests`, `CynoJumpHostTests.A_revelation_jumps_to_an_accessible_structure_beacon_as_it_does_to_a_field`, `StructureCynoBeaconSimTests.A_pharolux_beacon_is_placed_when_its_service_comes_online_and_taken_out_when_it_goes_offline`, `StructureCompositionTests.A_structure_cyno_beacon_jump_is_refused_by_name_through_the_registry_for_a_jammed_system_and_for_no_access`. Live client walk remains open.
- 1374 — Owner corporation fuel-burn notifications. Evidence: all three `StructureFuelOwnerNotificationTests` methods; hull fuel reductions remain open.
- 1450 — Analysis-beacon link lock, bounded to the live-tested lock and movement/warp refusal behavior. Evidence: `test_crab_walk.py` and simulation `LINK_HOLD` test; graphical cloak/tether and UI checks remain open.
- 1532 — Insurgency campaign start in each warzone, with the documented hourly interval and hub-choice shortcuts. Evidence: `InsurgencyDirectorTests.Each_warzone_with_systems_starts_one_campaign_in_forecasting_covering_its_hub_and_its_nearest_low_systems`; live `test_fob_walk.py` and `test_fob_insystem_walk.py`.
- 1534 — Existing pirate/empire site starts and once-per-system site scoring plus contributor kill reward. Evidence: `InsurgencyDirectorTests.A_campaign_starts_its_sites_when_it_becomes_active_and_a_completed_site_scores_its_system_once`, `InsurgencyDirectorTests.A_pirate_win_pays_each_contributor_the_victory_points_and_ends_the_other_sites`.
- 1552 — Basic level-4 mission dungeon start has a live headless walk. Evidence: `tools/headless-client/tests/test_mission_walk.py` (section 55); arc 29 mission 14117 remains open.
- 1554 — Talk-step completion and one-time reward. Evidence: `MissionDungeonRuntime.OnConversationAsync`, `AgentDialogueSources.OnConversation`, `Arc29StepsTests`.
- 1556 — Basic kill re-offer after the four-hour reference delay. Evidence: `BasicKillOffers.cs`, `BasicKillOffersTests` (5). Bonus/LP remains open.
- 1558, 1569 — Held-mission briefing and objective view. Evidence: `MissionViewsTests`, Host `ReadHeldMissionAsync`.
- 1559 — In-space agent placement and conversation. Evidence: `InSpaceAgentPlacementTests`, `Arc29StepsTests`; warp/approach remains open.
- 1592 — EveJS bonus interval unit is minutes, converted to EVE FILETIME ticks. Evidence: v0.12.9 `agentMissionRuntime.js` (`resolveMissionBonusTimeRemainingTicks`, `isMissionBonusRewardAvailable`) and 261018.1 `missionRuntimeState.js` (`normalizeMissionRewards`). Eligibility and payout remain open.

## Still open with evidence and reason

- 1241 — Ten raw cosmic-site difficulty values above V lack V24.01 confirmation; downtime has no runtime transition. The wiki sets I–V scan levels and says cleared sites respawn immediately, not after downtime; `SiteTemplate`, `CosmicSiteDirector`, and `CosmicSiteRulesTests` show current behavior.
- 1242 — Anchor set differs from EveJS: eve.rs reads planets/belts/stations/gates; both EveJS versions admit non-sun celestial rows. EVE University says “celestials (may only be planets),” so moon eligibility needs a V24.01 decision and regression.
- 1243 — No cosmic-site live scanner walk exists under `tools/headless-client/tests`; in-process code/warp tests do not verify overlay fields or end-to-end client warp.
- 1258 — Only SDE mission build 3405148 is pinned; no V24.01 build 3396210 `missions.fsdbinary` is present for a record-level comparison.
- 1260 — Agent-to-mission pool membership is absent from `MissionStaticCatalog`; EveJS route logic exists, but each pool needs source-backed candidate membership and offer regressions.
- 1262 — Three mission dungeon IDs do not resolve in the official dungeon table; authoritative NPC/site content is absent.
- 1263 — No V24 epic-arc journal text-ID mapping is in `MissionDataGaps.EpicJournalText`; resolve the client localization mapping and add a payload regression.
- 1264 — Mission LP bases remain configurable; dynamic inputs are not fully specified. ESI offer data omits LP-store standing requirements.
- 1369 — Compression backend is Host-tested, but `test_structures.py` has no live compression/corporation-office scenario.
- 1374 — No live `CmdJumpToStructureBeacon` walk verifies beacon choice, fuel debit and destination transition.
- 1377 — `StructureFuel.HourAsync` burns base `FuelPerHour`; SDE hull-bonus inputs and reduced-burn tests are absent. EVE University documents hull-specific service fuel reductions.
- 1455 — Analysis-beacon D-R1 payouts remain open: `Eve.Npc.PgBountyStore` handles ordinary NPC prizes, but `AnalysisBeaconDirector` has no bounty-credit path; EveJS v0.12.9 `mobileAnalysisBeaconRuntime.grantMobileAnalysisBeaconScanReward` grants item stacks only. The proposed prize table is owner-supplied with no source-backed amounts/recipient rules.
- 1456 — `AnalysisBeaconDirector` has no damage/target handler and the deployment is not a damageable simulation body; current Host tests cover links, scans and rewards only.
- 1457 — Graphical cloak/tether restrictions and the link UI, activation timer, scan UI and beacon ball kind have no matching live graphical walk; simulation tests cover some restrictions only. A split row also requires the same D-L2 scenario on EveJS v0.12.9; its character store is not seeded in the available environment.

- 1531 — Insurgency notice publication exists in Host composition, but no V24.01 live walk verifies client text, handling, recipients or order.
- 1534 — Corruption spread at level 3, per-stage contribution percentages, Ice Refinery Heist and Counter-Insurgency Outposts remain open because source-backed rules and producer regressions are absent.
- 1535 (BLOCKED) — Owner deferred C-G1 until the client gateway call path and truthful progress/threshold authority exist. Transport exists; insurgency handlers, cumulative progress owner and source-backed stage thresholds/effects do not.
- 1556 — Arc 29 mission 14117 remains held because template 3509 lacks Damsel content; the basic live walk does not cover it.
- 1557, 1561 — Timed bonus eligibility/settlement and LP need source-backed dynamic rewards and deadline/replay tests.
- 1564, 1566, 1575 — `GetReplayTimestamp`, `WarpToLocation` and `WarpToAgentInSpace` remain named refusals; no live agent approach walk exists.
- 1593 — Fetch/courier objectives lack authoritative item/destination mapping and the cargo-to-agent completion transition.
- 1594 — Mission dungeon payload still uses placeholder owner and zero location; resolve faction owner and active-instance placement from authoritative mission state.

## Verification in this run

CBM and Serena source/test declarations were inspected; external mechanics claims were checked against EVE University and EveJS. No PostgreSQL database was used and no tests were executed locally. Selected GitHub Actions run 39939081849 passed for the site, structure, and mission test filters. Run 39940442097 passed `Eve.Services.Space.Tests` (4/4) and `Eve.Insurgency.Tests` (7/7), with no failures or skips in either .NET project. Its generic fast live set reported 101 passed and 8 skipped; the skipped cases are recorded separately and do not count as passes. Neither run verifies the outstanding graphical client walks. No `ci.yml` run existed for the pushed SHA at the time of this check.
