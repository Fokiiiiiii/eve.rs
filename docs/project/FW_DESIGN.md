# Factional warfare: design and state

Scope: the Faction Warfare (FW) state that the V24.01 client reads and writes: the two warzones and their occupation,
enlistment of characters, corporations and alliances, the militia rank, the system LP pools and the LP donation,
the taxes that depend on that state, and the persistence behind them. This page records what is built, the sources
of each number and rule, the decisions where the sources differ, and what is not built.

## Sources and their priority

- Numbers that the client uses come from the V24.01 client code, `/home/user/eve-logic` (commit `1dbb5711`):
  `eve/common/script/util/facwarCommon.py` (upgrade levels, benefits, donation tax, enemy pairs, enlistment minimums),
  `eve/common/lib/appConst.py` 316 (`corpRoleDirector` = 1), 575-636 (faction ids), 1047-1059 (corporation status
  constants and the LP thresholds), `eve/client/script/ui/services/facWarSvc.py` (the `facWarMgr` calls),
  `marketutil/brokerFee.py` and `marketutil/skilllimits.py` (broker commission order), `industry/npsStationTaxModifier.py`.
- Faction ids, militia corporations and system factions: the pinned SDE (build 3396210, `sde/lock.json`): the
  `factions` table (`militiaCorporationID`) and `mapSolarSystems.factionID`. The client extract
  `/home/user/eve-client-sde/compat/factions.jsonl.gz` carries the same values and was used to check them.
- Warzone membership: the client's `warzonespositiondata` map (`/home/user/eve-client-sde/native/warzonespositiondata.jsonl.gz`,
  warzone 1: 103 systems, warzone 2: 149 systems). The same map is embedded for Eve.Insurgency as
  `dotnet/src/Eve.Insurgency/Data/warzone-systems.json`; Eve.FactionWarfare embeds that file (one source).
- Rules without a client constant: EVE University wiki, page "Faction warfare" (`https://wiki.eveuniversity.org/Faction_warfare`).
  The page marks its upgrade and donation sections as outdated. Where it disagrees with the client the decision is in
  `docs/reference/compat/DEVIATIONS.md`, section "Factional warfare".
- The live behaviour of EveJS v0.12.9 was not used for FW: it has no system upgrade, no donation tax and no occupancy state
  (DEVIATIONS says the same).

## Code

| Piece | File |
|---|---|
| Factions, militia corporations, enemy pairs | `dotnet/src/Eve.FactionWarfare/FwFactions.cs` |
| Formulas: upgrade level, fee and cost factors, donation tax, enlistment minimum, rank, contest, downtime | `dotnet/src/Eve.FactionWarfare/FwRules.cs` |
| Warzones, their systems, the initial occupier | `dotnet/src/Eve.FactionWarfare/FwWarzones.cs` |
| PostgreSQL store (schema `factionwarfare`, migration 0001) | `dotnet/src/Eve.FactionWarfare/FwStore.cs`, `Migrations/0001_factionwarfare.sql` |
| Transactions: donation, enlistment with the downtime, status, zone reads | `dotnet/src/Eve.FactionWarfare/FwService.cs` |
| The `facWarMgr` service (client calls) | `dotnet/src/Eve.Services.Core/FacWarMgrService.cs` |
| Host wiring: seeding, the occupation feed, the zone info, the market callback | `dotnet/src/Eve.Host/Composition/FactionWarfareComposition.cs`, `CoreRpc.cs`, `ItemRpc.cs`, `IndustryComposition.cs` (`GetFacWarZoneInfo`), `CorporationComposition.cs` |
| Broker fee discount | `dotnet/src/Eve.Market/MarketRules.cs` (`Context`), `MarketTrading.cs` (`FactionWarfareUpgradeLevel`) |

## State

- **Occupation.** `factionwarfare.fw_systems`: one row per occupied system of the two warzones: occupier, LP pool, the
  victory points of the occupier's enemy (`attacker_points`), and the update time. The first start seeds each warzone
  system whose SDE faction is one of the warzone's two factions (`FwWarzones.InitialOccupier`). A system with another
  faction, or none, is unoccupied and has no row. Later starts keep the stored state (the seed uses `ON CONFLICT DO NOTHING`).
- **Contest.** `FwRules.Contest(attackerPoints)`: stable at 0, contested below 75,000 points, vulnerable at 75,000 (wiki).
  `ContestedPercent` is the points share of 75,000, capped at 100.
- **LP pools and donations.** `fw_systems.lp_pool` is the pool; `fw_lp_donations` is the ledger. A donation debits the
  donor's LP with the militia corporation of the faction (`LoyaltyPointStore.ApplyAsync`, the same transaction) and adds
  the hub share to the pool. The upgrade level of a system is `FwRules.UpgradeLevel(pool)`; a system held by an enemy of
  the pilot's faction gives that pilot no level (`EffectiveUpgradeLevel`).
- **Enlistment.** `fw_enlistments` (kind 1 corporation, 2 alliance): `faction_id`, and a pending change
  (`next_faction_id`, `effective_ms`, `changed_ms`). A join or leave requested now takes effect at the next downtime
  (`FwRules.NextDowntimeMs`, 11:00 UTC). The change is applied when the row is next read at or after that time, so no job
  runs it. A pending join can be withdrawn while pending, and so can a pending leave.
- **Character enlistment.** The character's direct enlistment and its one-day cooldown stay in the Characters store
  (the store `fwCharacterEnlistmentMgr` already uses). `facWarMgr.JoinFactionAsCharacter` writes through the same
  character callbacks and sets the session's `warfactionid`.
- **Rank.** `FwRules.RankOf(standing)`: the whole part of the character's standing toward the militia corporation, 0 to 9
  (the client's rule). The standing is the relation store's `standing` (subject character, object militia corporation).

## facWarMgr surface

Served (each has a test): `GetCorporationWarFactionID`, `GetAllianceWarFactionID`, `GetFactionCorporations`,
`GetFactionMilitiaCorporation`, `GetCorpFactionalWarStatus`, `JoinFactionAsCorporation`, `JoinFactionAsAlliance`,
`LeaveFactionAsCorporation`, `LeaveFactionAsAlliance`, the four `WithdrawJoin*`/`WithdrawLeave*`, `JoinFactionAsCharacter`,
`GetMyCharacterRankInfo`, `GetCharacterRankInfo`, `CheckForRankChange` (returns None: no rank-change producer),
`GetSolarSystemLPs`, `GetSolarSystemLPsEx`, `DonateLPsToSolarSystem`, `GetControlBunkerIDInSystem` (None: eve.rs has
no control bunker structures), `RefreshCorps` (None: the state is always current).

Corporation join, leave, and withdrawal mutations require the session Director bit and recheck current persisted corporation membership and Director authority. Alliance mutations also require current Director authority in the persisted active executor corporation. The faction-warfare regression denies stale corporation and alliance sessions after their persisted role checks refuse them.

Statistics, served from the daily rows and the faction and alliance data (slice 2): `GetStats_Character`, `GetStats_Corp`,
`GetStats_Alliance` (the sum over the corporations that are members of the caller's alliance now), `GetStats_Militia` (header: the occupier factions;
per key one value per faction, summed over its enlisted corporations), `GetStats_FactionInfo` (per faction: `totalMembersCount`,
`militiaMembersCount`, `topMemberCount`, `systemsCount` as util.KeyVal), `GetStats_TopAndAllKillsAndVPs` (two dicts keyed by group 1, 2, 32: the top
and the sum). Rank overview: `GetMyCharacterRankOverview` and `GetCharacterRankOverview` (a list of util.KeyVal `factionID`, `currentRank` for each
occupier where the rank is above 0). Not served: `GetStats_CorpPilots` (no client consumer found; the dashboard derives the same numbers from
`GetStats_FactionInfo`).

Refusals reach the client as `CustomInfo` notices (text keys of the client's localization), `FacWarDonationTaxChanged`
(with `taxNow` in percent, the client's retry key) and not as localized pool messages.

## Taxes

- **Broker fee.** The station commission is 3 percent, less the Broker Relations skill, times `1 - 0.1 * level`, less the
  standing discounts (the client's order in `brokerFee.py`). The level is the system's `EffectiveUpgradeLevel` for the
  character's war faction (direct enlistment, else the corporation's), read in the market transaction.
- **Hub donation tax.** The tax is `FwRules.DonationTax(faction, held, warzoneSize)`: the client's formula on the share of
  the warzone's systems the faction holds. The donation must quote the tax; a stale quote is refused with the tax now.
- **Zone info.** `map.GetFacWarZoneInfo` returns `factionPoints` (held systems), `maxWarZonePoints` (warzone size) and
  `systemUpgradeLevel` (per held system), the fields the client reads.
- **NPC station war-faction modifier.** Not wired (see "Not built").
- **Corporation LP tax.** Not applied (see "Not built").

## Tests

- `dotnet/tests/Eve.FactionWarfare.Tests`: 44 pure rule and map tests, 9 PostgreSQL tests of the enlistment timing,
  the donation rules and the seeding (eve_p4), 3 `facWarMgr` RPC tests (director role, pending join and the status
  record, character enlistment and rank, a stale donation quote and an accepted one).
- `dotnet/tests/Eve.Market.Tests/FactionWarfareBrokerFeeTests.cs`: 3 tests of the broker discount and its order.
- `Eve.Services.Core.Tests` (752) and `Eve.Market.Tests` (111, one benchmark skipped as before) pass.
- Host: `Eve.Host.Tests` runs the composition and the live-script isolation test (`EVE_FACTIONWARFARE_SCHEMA` is in
  `tools/dev/run-eve-live.sh`). Result on the merged tree (2026-10-17): 348 passed, 58 failed of 406. All 58 failures are
  PostgreSQL error 53300 "sorry, too many clients already" on the shared cluster (max_connections 100, other agents connected),
  not a factional-warfare logic failure. Not hidden: the Host gate is not green on this cluster. Rerun on a quiet cluster or with
  fewer concurrent rigs before treating the Host suite as passing.
- Market: `Eve.Market.Tests` has 5 failures in `MarketCoherenceTests` (PyList expected, PyTuple received, from origin/main
  commit 88256991, server-to-client call arguments became tuples). Owned by the fix agent; not changed here.

## Built since the first slice

- **Statistics** (migration 0002): `GetStats_Character` and `GetStats_Corp` serve the six client keys from daily rows. The
  producers `FwService.RecordActivityAsync` and `FwService.AddVictoryPointsAsync` fill them; no combat event calls them yet.
- **Capture** (`FwService.CaptureAsync`): a vulnerable system (75,000 attacker points) passes to the occupier's enemy, its points
  and LP pool reset. The trigger (hub destruction) is not connected.
- **NPC station war-faction factor**: applied to the NPC station facility tax of industry jobs for the pilot's war faction.
- **Corporation LP tax**: `LoyaltyPointStore.CreditMemberAsync` takes the member's corporation rate of an LP credit into the
  corporation's own balance. Applied to the insurgency victory payout; the Viridian expansion notes give the scope.

## Complexes (slice 1)

- Rules: `dotnet/src/Eve.FactionWarfare/FwComplexes.cs` (sizes, capture times, hull limits, victory points, the timer, the resolution).
  The sources and the project rules are in DEVIATIONS "Factional warfare" (Complexes).
- State: `fw_complexes` (migration 0003): one open complex per size in each occupied system; `progress_ms` is signed (attackers positive).
- Tick: `FwComplexDirector` registers `factionwarfare.complexes.tick` (every 10 s). `FwComplexTicker` spawns the missing complexes (after the respawn
  time), advances the timers from the pilots present, and resolves a complex at its capture time. Resolutions are published in the job transaction
  (`factionwarfare.complex_resolved`) and notified after the commit (`OnFacWarDungeonCompleted`, `OnVictoryPointStateUpdated`).
- Victory point state: `fwVictoryPointMgr` reads the store (attacker points against 75,000).
- Dungeon sites: each complex is a dungeon instance (`FwComplexSites`, host); its waves are the NPC defenders; presence is the pilots whose
  current dungeon is that instance; the timer waits for the defenders. Open complexes are in the system anomaly overlay (scan visibility).
- Live walk (eve_p4 lane, live port 26103): `tools/headless-client/tests/test_fw_complex_walk.py` passes (1 of 1): every occupied system has a
  victory point state at the 75,000 threshold with no points on a fresh server.
- Capture walk: `tools/headless-client/tests/test_fw_capture_walk.py` passes (eve_p4, MITM capture): enter the gate, clear the defenders with the QA
  Smartbomb, the timer runs, `OnFacWarDungeonCompleted` is sent. The helpers are `tests/fw_complex_live.py`. Per-pilot victory points: the winning side's pilots inside split the victory points evenly (rounded down) on `fw_pilot_daily`
  (`FwComplexes.cs`, DEVIATIONS "Complexes: victory points"); the walk asserts the pilot's row and `GetStats_Character`.
- Battlefields: built, see "Battlefields" below (archetype 68 is the battlefield's gated site; the objectives, archetype 67, are not built).
- Tests: `FwComplexTests.cs` (5). Eve.FactionWarfare.Tests: 83 passed on eve_p4.

## Not built

- **Pilot-versus-pilot kills**: not counted. The NPC kill producer (`RecordFactionWarfareNpcKill`) is built; a player kill has no
  statistics or victory-point path yet (no event for it was found).
- **Victory points from complexes**: built (gate, NPC defenders, per-pilot split). Battlefields use the 50,000 LP pool documented below. The ship-kill LP formula (wiki: 3.25 times hull, insurance and fitting value over 10,000,
  capped at 50,000 LP) is not built.
- **Capture trigger**: infrastructure hub destruction (25 million HP, wiki); no FW hub combat path exists.
- **Statistics**: built in slice 2 (above). `GetStats_CorpPilots` is not served (no consumer).
- **Mission LP credits**: mission turn-in credits the issuing corporation's LP through `LoyaltyPointStore` and applies the member corporation's LP tax in the same completion transaction. Report-derived base tables and formula factors are configurable approximations; SDE `extraStandings` supplies mission standing entries, with configurable fallback values only for basic Level 4/5 missions.
- **Live verification**: the scout-complex capture walk passed on eve_p3 on 2026-10-20; focused `FwBattlefield` tests passed 9/9 on eve_p4 on 2026-10-23. Graphical-client verification remains open.
- **Earlier Host suite run**: 58 of 406 failed on connection exhaustion (see the testing section above); not a FW logic failure, but not green.
## Acceleration gates (pointer)

Pilots enter a complex's rooms through acceleration gates. The API for the FW complex director is in
`docs/project/ACCELERATION_GATES.md` (`DungeonRoomLayout`, `SpaceContainers.AccelerationGateDeployableId`,
`SpaceAccelerationGates`). Single-room complexes need no gate.

## Battlefields (built, C3a to C3b; walked live)

- Client contract: `spacecomponents/client/components/fwCapturePoint.py` (the point's slim fields `component_fwCapturePoint_factionInfo` (attacker,
  occupier) and `component_fwCapturePoint_nextPointTime`; the point is contested when both sides' pilots are within its range) and `fwScoreboard.py`
  (`component_fwScoreboard_score`, signed: negative for the occupier, positive for the attackers, between the thresholds of the SDE scoreboard).
- SDE: the control zone types (`{Faction} Control Zone A/B/C`, group 4494) carry `fwCapturePoint` (numberOfSeconds 20, pointValue 1, range 30 km); the
  scoreboards (types 73447 to 73450, group 227) carry `fwScoreboard` (thresholds -150 and 150). The battlefield's site is a dungeon of archetype 68 (nine
  templates in the owner catalog, each with one gate).
- Rules: `dotnet/src/Eve.FactionWarfare/FwBattlefields.cs` (`FwBattlefieldTicker`). Up to two open battlefields, one per occupied system, a new one three
  hours after a completion. Three points at the centre and 80 km either side of the battlefield's origin; the holder of a point is the side whose pilots are
  within its range alone, its hold timer runs and each full 20 s of holding moves the score one point toward that side; both or neither pause it. A score of
  +/-150 wins the battle: 2,000 victory points move the system's attacker points, the winners' victory points (split evenly) go to `fw_pilot_daily`, and the
  first 30 winning pilots (by character id) get 1,666 LP each from the 50,000 LP checkpoint pool, issued by the winning militia; 20 LP remains undistributed (`IFwRewards`, the loyalty store with the member's corporation tax).
- Site: the battlefield's origin is the complexes' site point of its system moved 300 km along x (`FwBattlefields.SiteOffsetMeters`). The site starts with the
  first pilot in the system (`IFwComplexSites.StartBattlefieldAsync`, as a complex's does), ends when the battlefield is won, and its gate is the room's
  (`AccelerationGateDeployableId`). A pilot's current site is the open complex or battlefield whose capture radius holds it (`FwComplexSites.InstanceOfAsync`).
- State: migration 0005 (`fw_battlefields`, `fw_battlefield_points`, `fw_battlefield_pilots`), migration 0006 (`fw_battlefields.instance_id`), `FwStore.Battlefields.cs`.
  The director (`FwComplexDirector`) ticks it in the complex tick's transaction and publishes `factionwarfare.battlefield_state` and `factionwarfare.battlefield_won`.
- Host: `NotifyBattlefieldState` places the points (`PlaceCapturePoint`, protocol 77), the scoreboard (`PlaceBattlefieldScoreboard`, protocol 78), sets
  the next tick and the score (`SetSlimField`), and takes the balls out when the battle is over. `NotifyBattlefieldWon` sends `OnFwScoreboardWon` and
  `OnVictoryPointStateUpdated` to the system's pilots.
- Simulation: a deployable reaches the pilots that come into its view after it is placed (`reconcile_deployables`, found by this walk: before, a pilot that
  flew into a gate's or a point's box was never sent its ball). The deployable's view is kept the same way as the wrecks' and the asteroids'.
- Tests: `FwBattlefieldRulesTests` (pure), `FwBattlefieldTickerTests` (eve_p4: a side's win with the awards, the occupier's win, the pause under contest, the
  site started with the first pilot and ended with the win), `SimCodecTests` and the golden frames for protocols 77 and 78, the Rust test
  `a_deployable_reaches_a_pilot_that_comes_into_its_view_and_leaves_with_it`, `a_capture_point_...` and `a_battlefield_scoreboard_...`.
- Live walk: `tools/headless-client/tests/test_fw_battlefield_walk.py` (eve_p4, the live port 26103, MITM 26113, run with `EVE_FW_CAPTURE` set to the capture
  file). A pilot enlists in the occupier's enemy, flies to the battlefield's system, warps to its gate and takes it into the room. The capture holds each point's
  faction info and tick countdown and the scoreboard's score updates. The centre point is held by the enemy while the pilot is in its range (the room's
  entry is 15 km from it), its score rises by one per 20 s, and the battlefield is won at +150: the test asserts the system's attacker points (+2,000), the
  notices (`OnFwScoreboardWon` before `OnVictoryPointStateUpdated`), the winner's victory points on the daily row and in `GetStats_Character`, and the 1,666 LP
  in the wallet journal. The walk takes about 50 minutes (the win needs 3,000 s with one point held). The walk also checks the two side points and that contesting the centre pauses its score.
- Not built: the battlefield's NPC blockers (enemy NPCs do not pause a point), the defenders of a point, the frontline choice (every occupied
  system is a candidate), the objectives (archetype 67) and their `OnFwObjectiveCompletedInSolarSystem`, and a non-enrolled capsuleer landing 75 to 125 km from the points. The gate admits T1 hulls (SDE meta group 1 or no meta group) and empire Navy hulls (meta group 4, faction IDs 500001 through 500004); `AccelerationGateTests` covers accepted and refused hulls.
- Checked by the live walk: the two side points' holding and the centre contest. Checked by `FwBattlefieldTickerTests`: the timer pause under contest and the cooldown-created second battlefield's victory.


## Research reconciliation (2026-10-19)

Owner decision Q-16 applies the checkpoint per item: build 3396210 uses a 50,000 LP pool per battlefield; the 150,000 increase began on 2026-07-01. `FwBattlefields.LpPool` is 50,000, divided into integer 1,666 LP shares for up to 30 winners (49,980 LP paid when all 30 qualify). The 30 km capture, three-hour cooldown, 2,000 VP and 30-recipient maximum remain as implemented; blocker details and pod eligibility still need comparison to the current walk.
