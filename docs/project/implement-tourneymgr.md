# tourneyMgr client flow audit

Date: 2026-11-13. Client target: EVE Online V24.01, build 3396210. This audit covers all `tourneyMgr` client callsites found across `/home/user/eve-logic`, the Host registration/provider path, the EveJS v0.12.9 comparison, and the regressions added or rerun for this task.

## Registration and current implementation

`CoreServices.Create` registers `TourneyMgrService` (`dotnet/src/Eve.Services.Core/CoreServices.cs:79`); the service advertises `[Service("tourneyMgr")]` and its RPC methods in `UnportedFeatureRpcServices.cs:551-700`. Host composition constructs `PersistenceTournamentStore` over the configured tournament tables and the pinned SDE, wires `QueryAsync`, runs tournament match migrations, constructs `PersistenceTournamentMatches`, and passes its callbacks into `CoreServices.Create` (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:201-238,459-465`). The store returns configured tournament, series, match, arena-system and ship data from PostgreSQL/SDE (`dotnet/src/Eve.Host/Persistence/Tournaments/PersistenceTournamentStore.cs:20-140`). Match actions, autobans, fleet consent and referee/camera calls use the persisted match store rather than a second in-memory gameplay store.

The client-side calls and consumed shapes are:

| RPC | V24.01 callsite and arguments | Result consumed by client |
|---|---|---|
| `GetTourneySetup` | `eve/devtools/script/tournamentRefereeTools.py:537-540`, no application arguments | 3-tuple `(tournaments dict, tourneySystems dict, activeSystems list)`; the UI iterates tournament IDs/names and system IDs/beacons/bubbles and checks active systems (`:538-563`). |
| `GetPotentialMatches` | `tournamentRefereeTools.py:552` and `:575`, one `tourneyID` | Mapping keyed by `seriesID`; values provide `redTeam`, `blueTeam`, and `system` (`:575-587`). The two source callsites are tracked separately in the coverage CSV. |
| `GetAllCurrentSeriesMatchDetails` | `tournamentRefereeTools.py:577`, one `tourneyID` | Mapping keyed by `seriesID`; each value is indexed at `[0]` for current match index and `[1]` for match count (`:577-583`). |
| `QueryTournamentDetails` | `tournamentRefereeTools.py:590`, one `tourneyID` | Dict fields `allowedShipList`, `maximumPointsMatch`, `matchLength`, `overtimeLength`, and `externalRulesetID`, consumed at `:590-600`. |
| `QueryShipList` | `eve/client/script/ui/extras/tourneyUISvc.py:30-34`, one `tourneyID` | Dict field `allowedShipList`, passed to the ban UI. |
| `BanShip` | `eve/client/script/ui/extras/tourneyBanUI.py:63-73`, positional `banID` and `banTypes` list | Reply is ignored; the client closes the modal. `banTypes` contains selected ship type IDs, or an empty list if no option is selected. |
| `ExecuteMemberChange` | `eve/client/script/ui/extras/tourneyUISvc.py:38-45`, no application arguments, sent only after the player accepts `OnChangeFleetBoss` | Reply is ignored; the client-side consent dialog only calls it on YES. |

The full referee moniker/bound-object method inventory, argument observations and existing per-call coverage are retained in `EVE_RS_COVERAGE.csv` (RPC-02705–RPC-02745 plus RPC-02845–RPC-02849). The client creates a match with six positional arguments at `tournamentRefereeTools.py:817-824`; autoban startup uses the two positional arguments and three named values at `:1028-1030`. Camera callers are in `eve/client/script/ui/extras/form_tournament.py:130-141,243-263,333,615-620`. No `OnRemovalAsRef` caller exists in the whole client tree search; this audit does not infer a notification producer from that name.

## State, errors and notifications

Queries are read-only and have no mutation notifications. The production query callback is connected; an absent tournament row is represented by the service's documented empty/default fallback rather than a fabricated tournament.

`BanShip` validates its two-argument shape and character session, then routes to `PersistenceTournamentMatches.SubmitBanAsync` through the Host callback (`TourneyMgrService.Autobans.cs:10-17`; `HostComposition.cs:460-462`). The persisted handler checks the active ban/session and ship type, commits the ban once, and publishes the match update. Repeating the same accepted choice is idempotent; a different pilot, stale session, or invalid type errors. The client-visible prompt is delivered as `OnTournamentPerformBan`, and committed match changes as `ProcessTournamentMatchUpdate`.

`ExecuteMemberChange` requires an argument-free request from a live character/session and routes to `AcceptFleetAsync` (`TourneyMgrService.Fleet.cs:26-34`; `HostComposition.cs:460-462`). It applies the pending captain nomination only after the nominated member's consent, persists the captain/fleet roster, clears the pending nomination, and publishes the resulting `ProcessTournamentMatchUpdate`. A stale or unrelated session errors instead of acknowledging an unapproved change.

## EveJS comparison

`/home/user/evejs-261108.1/server/src/services/tournament/tourneyMgrService.js:83-134` defines the same five read methods. Those methods return empty/default data and record audit events. Its `BanShip` and `ExecuteMemberChange` handlers log/audit and return `null` without tournament-state changes (`:121-134`). Eve.rs retains the client-compatible return behavior (`None` for the two no-result calls and the same empty/default read wire shapes when no provider result is available) while using configured PostgreSQL/SDE state and real persistent, authorized mutations for these actions. This is an intentional functional improvement over EveJS's no-state acknowledgements, consistent with the client's ban/consent flow and the existing match state owner.

The EveJS source was inspected; its live runtime was not executed for this task.

## Regression evidence

The query test exercises registered Host RPC dispatch against persisted tournament/series fixtures and the pinned SDE ship type; the three previously missing client callsites are now explicit coverage rows. The action test creates the real `CoreServices` registry and `PacketDispatcher`, sends encoded `CallReq` packets, and checks reply envelopes, invalid/stale caller errors, PostgreSQL ban/fleet mutations, retries, and notification counts/order. These are headless Host checks, not graphical-client verification.

Database: dedicated `eve_p3` at `postgres://postgres@127.0.0.1:5432/eve_p3`. The official client SDE build 3396210 was loaded from `.cache/sde/eve-online-static-data-3396210-jsonl` (61 tables, 668,618 records; database only, not repository data). The Host composition test attaches both SDE schemas, so official mission build 3405148 was also loaded into `eve_p3` from CCP's static-data endpoint (10 tables, 57,803 records; database only, not repository data).

### Checked

- `TournamentMatchActionTests.Bans_fleet_consent_and_countdown_are_transactional_authorized_and_retry_safe`: 1 passed, 0 failed, 0 skipped. Release Host test project build: 0 warnings, 0 errors.
- `AbyssalHostFlowTests.Tournament_camera_reads_locked_roster_and_actual_match_state_through_registered_wire`: 1 passed, 0 failed, 0 skipped with the Rust `eve-sim` binary and pinned SDE configured.
- Targeted Host slice (`Tournament_setup_and_matches_use_the_mappings_consumed_by_the_client_through_host_dispatch`, `Tournament_ship_list_uses_the_dictionary_field_consumed_by_the_client_through_host_dispatch`, `TournamentMatchActionTests`, the camera test above, and `TournamentRegionCompositionTests`): 5 passed, 0 failed, 0 skipped. This checks both methods in `TourneyMgrHostWireTests`, the two newly wired service actions, persisted query shapes, and Host composition restart on the dedicated PostgreSQL lane.
- A broader name-based filter was started, then cancelled before completion because it selected the full live `AbyssalHostFlowTests` group. Its incomplete result is not counted; it initially exposed the missing `sde_mission` test-database setup, which was corrected before the targeted 5-test rerun above.

### Not checked

- Graphical V24.01 tournament referee, ban, fleet-consent, and camera UI against a running eve.rs server.
- Live EveJS differential execution.
- Whether tournament UI workflows require any additional client behavior not represented by the complete V24.01 callsite search and the existing focused Host test inventory.
