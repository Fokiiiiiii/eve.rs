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
- Not built: the acceleration gate and the warp into a complex, NPC defenders, the battlefield (archetype 68) and the objectives (67), the
  complexes in the scan data, the per-pilot victory points of a capture, a live walk.
- Tests: `FwComplexTests.cs` (5). Eve.FactionWarfare.Tests: 83 passed on eve_p4.

## Not built

- **Pilot-versus-pilot kills**: not counted. The NPC kill producer (`RecordFactionWarfareNpcKill`) is built; a player kill has no
  statistics or victory-point path yet (no event for it was found).
- **Victory points from complexes**: built (Complexes, slice 1); the gate, the NPC defenders and the battlefield are not. The ship-kill LP formula (wiki: 3.25 times hull, insurance and fitting value over 10,000,
  capped at 50,000 LP).
- **Capture trigger**: infrastructure hub destruction (25 million HP, wiki); no FW hub combat path exists.
- **Statistics**: built in slice 2 (above). `GetStats_CorpPilots` is not served (no consumer).
- **Mission and incursion LP credits**: no credit source in eve.rs, so the corporation LP tax applies to insurgency payouts only.
- **Live walk**: no headless-client FW walk and no live server run for this work. The live runner needs a Rust build (several GB of
  target output) and a quiet PostgreSQL cluster; the shared cluster hit its connection limit during the Host suite.
- **Host suite**: 58 of 406 failed on connection exhaustion (see the testing section above); not a FW logic failure, but not green.