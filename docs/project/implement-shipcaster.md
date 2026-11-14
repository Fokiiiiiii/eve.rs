# Shipcaster RPC implementation record

Date: 2026-11-14

Client: EVE Online V24.01, build 3396210

Reference checkout: `/home/user/evejs-261108.1`

## Registration and implementation

Before this change, `CharacterServicesComposition.AgentRpcServices` registered all four RPC services through `CreateShipcasterServices`, but the production composition supplied no deployed pad/launcher data. Read calls therefore reported missing backends; `CanCharacterJump` returned `False`; both travel calls used EveJS's refusal notification.

Production applies the shipcaster migration with the existing `Eve.Ships` migration set (`PgSpaceItemStore.MigrateAsync` embeds every `Migrations/*.sql` file), then registers the store-backed services from `AgentRpc.cs:138-171,242-243`:

| Service | Client calls | Current Host path |
|---|---|---|
| `shipCasterLandingPadMgr` | `GetAllLandingPads()`, `GetFactionLandingPads(factionID)` | Reads published `shipcaster_landing_pads`, validates faction/system IDs, sorts and returns `LandingPadData` ObjectEx1 rows in `ShipcasterRpcService.cs:61-68,99-108,114-130`. |
| `shipCasterLauncherMgr` | `GetFactionsWithShipcaster()` | Reads distinct regular-launcher factions from PostgreSQL and returns valid warfare factions in `ShipcasterRpcService.cs:70-82`. |
| `shipcasterTravelMgr` | `CmdJumpThroughShipcaster(shipcasterID, toSolarsystemID, landingPadID)` | Validates the published launcher, active FILETIME schedule, exact requested pad/system, pad state, pilot's current FW faction and 5 km interaction range; then calls `SpaceSessions.StructureJumpAsync`. Session retries replay the accepted result. |
| `starterShipcasterTravelMgr` | `CanCharacterJump()`, `CmdJumpThroughStarterShipcaster(shipcasterID)` | Eligibility checks for an unused starter launcher in the character's current system. Travel validates the destination, claims a unique character row, then commits that row only after simulation acceptance. A refusal releases the claim. |

`PgShipcasterStore.PublishSnapshotAsync` replaces pads, launchers and schedules in one transaction and marks the snapshot published. It validates the three-target limit, non-overlapping windows and pad references. Active windows use `startsAt < now < endsAt`, matching the V24 component's strict comparison. `shipcaster_starter_jumps` persists pending/used state across process restarts. An uninitialized network does not produce fabricated empty lists: read methods return `CustomNotify`; travel returns the reference's client-visible `CustomNotify` failure. There is no fee. CCP describes travel as free, one-way, restricted to linked active destinations; the four empire shipcasters operate in Mehatoor, Onnamon, Intaki and Amo ([CCP shipcaster overview](https://www.eveonline.com/news/view/introducing-the-interstellar-shipcaster)).

## V24.01 client contract

The service wrapper in `eve/client/script/parklife/shipcasterSvc.py:18-37` is the source for all six method signatures, argument order and session-change wrappers:

| RPC | V24.01 contract |
|---|---|
| `GetAllLandingPads()` | No arguments; list of `shipcaster.landingPadData.LandingPadData`. |
| `GetFactionLandingPads(factionID)` | One faction ID; same list shape, filtered by faction. |
| `GetFactionsWithShipcaster()` | No arguments; list of faction IDs. |
| `CmdJumpThroughShipcaster(shipcasterID, toSolarsystemID, landingPadID)` | Three positional arguments, passed through `PerformSessionChange('jump', ...)`. |
| `CanCharacterJump()` | No arguments; boolean. |
| `CmdJumpThroughStarterShipcaster(shipcasterID)` | One positional argument, passed through `PerformSessionChange('jump', ...)`. |

`shipcaster/client/components/shipcasterLauncher.py:36-46` reads a launcher faction and its extra linked factions; `:126-186` consumes `component_shipcaster_linked_targets` and computes the active target; `:233-251` refuses charging launchers and requires the pilot faction to be eligible and to match the target faction. The client admits the six warfare factions represented by `FwFactions.IsWarfareFaction` (four empire factions plus Angel and Guristas). The server reads the matching faction and target-window data from its published state.

`shipcaster/client/components/starterShipCaster.py:51-64` reads a starter launcher destination system and landing-pad ID from component attributes. `shipcaster/shipcasterUtil.py:38-53` reads the same component state. The RPC only supplies the launcher item ID, so the server resolves the destination from the published launcher record.

`shipcaster/landingPadData.py:8-16,45-68` defines the six positional row fields and the meanings of `linkTimestamp`, `isLinked` and `canBeLinked`. The Host retains the `ObjectEx1` class token and the item/system/faction/built/link-time/disrupted order. The client reads the map lists in `fwwarzone/client/dashboard/shipCasterVisualization.py:57-65`, `warzoneMapPanel.py:398-425` and `warzoneInfoSideCont.py:229`.

`eve/client/script/ui/services/menusvc.py:1749-1762,3070-3076` and `menuSvcExtras/movementFunctions.py:602-617` trace the menu action to the respective RPC. `shipcaster/shipcasterConst.py:6-17` supplies the three-link cap, 60-second target/idle intervals and 5,000 m interaction range. The server applies that interaction range using the authoritative scene positions.

The V24 wrapper contains no callsite for `CanCharacterJump` beyond its declaration. The starter component itself returns true from `OfferJump` (`starterShipCaster.py:51-52`), while its destination comes from component attributes (`:54-60`). The Host's rule, "an unused starter launcher exists in this character's current system", is therefore an implementation policy inference rather than a verified client-derived eligibility rule. CCP's Exordium announcement describes the four rookie shipcasters as single-use per character and names their HQ destinations, but it does not define this RPC's policy ([CCP developer post](https://forums.eveonline.com/t/introducing-exordium/509843/304)).

## EveJS comparison

`shipCasterLandingPadMgrService.js:13-26` and `shipCasterLauncherMgrService.js:13-16` serialize runtime data. `shipcasterRuntime.js:30-33,180-220` keeps seeded lists and starter eligibility in process-local maps/sets; its test seed hooks do not provide a production network source. I executed those handlers against a seeded pad/faction state: the `LandingPadData` ObjectEx1 row, faction list and starter boolean match eve.rs's Host assertions. `shipcasterTravelMgrService.js:23-33` and `starterShipcasterTravelMgrService.js:23-36` always reject travel with `CustomNotify` ("No active shipcaster destination is available." / "No starter shipcaster destination is available."); the runtime has no successful travel implementation. eve.rs supports a success when the durable snapshot contains a currently active valid target. It uses the existing `SpaceSessions.StructureJumpAsync` transition, persists starter one-time use and arrival, and emits `OnJumpQueueUpdate`. This is an intentional difference from EveJS's stub behavior, based on the V24 session-change callsites and CCP's documented one-way shipcaster gameplay. It does not claim a graphical-client verification.

No EveJS successful shipcaster travel trace or successful starter-eligibility scenario exists in the reference checkout. In particular, the chosen `CanCharacterJump` policy cannot be independently confirmed against EveJS production behavior.

## Host E2E coverage

With PostgreSQL on `eve_p4`, the production factory and real Host dispatcher pass these flows in `ShipcasterNetworkHostWireTests.cs`:

- `Registered_shipcaster_queries_serialize_the_persisted_network_snapshot` calls all three read methods and asserts faction filtering, ordering and the six-field ObjectEx row.
- `Registered_shipcaster_jump_checks_the_published_target_and_persists_arrival` checks the session-change tuple, `OnJumpQueueUpdate`, same-session replay and persisted character/ship system after `SimJumped`.
- `Concurrent_shipcaster_retries_submit_one_jump_and_replay_the_session_change` sends duplicate calls together and asserts one simulation jump, one notification and matching results.
- stale-window, exact-window-start, client destination mismatch, wrong-war-faction and out-of-range tests assert `CustomNotify` and no simulation movement.
- `Registered_starter_shipcaster_is_one_time_and_keeps_eligibility_persisted` checks eligibility, the exact invalid-launcher `CustomNotify` text, simulation refusal/claim release, successful retry, persistent one-time use after store recreation, notification and duplicate-result replay.

To check that the success-path E2E assertions detect stubs, I temporarily registered the four RPCs without their backends. The query, regular-travel and starter-travel PostgreSQL Host E2E tests failed with the expected Host `ErrorResponse` (3/3); after restoring the production factory, all three passed. The exact-start-boundary regression also failed before the store query changed from `starts_at_filetime <= now` to `< now`, then passed with the V24 comparison.

The test network snapshot is explicitly published by the same PostgreSQL store used by the production handlers. The tests exercise real Host registration and DB state; they do not claim a graphical-client run. A process crash after a starter claim is committed but before the simulation acceptance callback completes leaves a pending claim that blocks a second attempt; startup recovery for that distributed-operation window is not implemented.

Local verification after merging the current `origin/main` passed with no skipped tests: Release builds with warnings treated as errors for `Eve.Ships`, `Eve.Services.Space`, `Eve.Host`, `Eve.Services.Space.Tests` and `Eve.Host.Tests` all reported 0 warnings; PostgreSQL-backed Host tests matching `FullyQualifiedName~Shipcaster` passed 13/13 against `eve_p4`; `ShipcasterRpcTests` passed 16/16; and movement/jump-bridge related Host regressions passed 3/3. The Host error checks assert the `CustomNotify` key and its client-visible message for unavailable snapshots and rejected regular/starter destinations.

## Remaining data-source gap and coverage status

The pinned SDE build `3396210` contains launcher types `76465–76468` and `79018`, rookie launcher types `93968`, `93980`, `93984`, `94018`, and beacon types `75928–75931`; the launcher descriptions identify faction HQ systems. It has no deployed launcher/pad item IDs, live beacon build/disruption/link state or active target windows. The JSONL export includes `mapSolarSystems`, `mapPlanets` and related celestial tables but no `mapDenormalize` deployment rows. CCP says beacon sites are chosen using dynamic system activity, location and advantage score, and beacons can be attacked and destroyed ([overview](https://www.eveonline.com/news/view/introducing-the-interstellar-shipcaster)); those runtime values cannot be reconstructed from type IDs alone.

A whole `dotnet/src` search also found no existing shipcaster world-state owner or caller of `PublishSnapshotAsync`; the new store currently has no production snapshot publisher. Consequently the deployed Host has no verified state to publish, returns `CustomNotify` for uninitialized read authority and cannot offer a live shipcaster network. No item IDs, positions, schedules or built-pad states were guessed or hardcoded. Until the production world owner supplies the snapshot, the shipcaster rows remain `HOLLOW` despite the persisted handlers and Host E2E coverage.

The requested `structureJumpBridgeMgr` HOLLOW rows are not in the checked-in CSV: rows `RPC-02667` and `RPC-02668` for `CmdJumpThroughStructureStargate` are already `IMPLEMENTED-TESTED`; its getter rows are also implemented-tested. No structure-jump-bridge implementation or status was changed here.

Not verified: a graphical V24.01 client run, live production shipcaster instance/schedule publication, a successful EveJS shipcaster travel flow, the server-side policy behind `CanCharacterJump`, and recovery if the process crashes after a starter claim is written but before the simulation acceptance callback completes (that leaves a pending claim until manual recovery).
