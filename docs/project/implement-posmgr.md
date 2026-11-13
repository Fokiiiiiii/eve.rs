# posMgr implementation audit

Date: 2026-11-13

## Scope and initial registration

At the worktree baseline (`HEAD`), `PosMgrService` only registered `AnchorStructure`, `UnanchorStructure`, and `GetControlTowers` (`dotnet/src/Eve.Services.Core/PosMgrService.cs`, baseline lines 23-95). `CoreServices` already registers the service (`CoreServices.cs:67`); the gaps were the client POS settings/production calls and planet orbital calls. No `posMgr` implementation exists in `Eve.Structures`; that project owns Upwell structures. `crates/eve-space` contains a control-tower simulation role, not the V24 POS RPC owner.

The user-provided coverage file had 18 `MISSING` rows for `posMgr`, representing 17 distinct methods because `SetTowerNotifications` has two client callsites. The duplicate `GetControlTowers` rows were already implemented before this work.

## Client call trace

The V24.01 client callsites were checked across `/home/user/eve-logic`, not only the coverage file:

| Client path | RPC contract and client use |
| --- | --- |
| `ui/services/menusvc.py:3667-3675` | `AnchorOrbital(itemID)` and `UnanchorOrbital(itemID)`, no result consumed. |
| `ui/shared/planet/orbitalMaterialUI.py:161-164` | `OnlineOrbital(itemID)`, no result consumed; the UI closes after issuing it. |
| `ui/inflight/moonmining.py:541-562` | `GetTowerSentrySettings(towerID)` is iterable as five values and also exposes `line`; fields are standing, security status, status-drop, war, and standing owner. |
| `moonmining.py:579-590` | `GetTowerNotificationSettings(towerID)` returns `showInCalendar` and `sendFuelNotifications`; `GetConsumptionFactor(towerID)` supplies the multiplier for displayed fuel rows. |
| `moonmining.py:704-742` | `GetStarbasePermissions(towerID)` returns deploy flags and an indexable `structureID` rowset; `SetStarbasePermissions(towerID, deployFlags, usageFlags)` saves them. |
| `moonmining.py:1136` | `SetTowerSentrySettings(towerID, standing, status, statusDrop, war, useAllianceStandings)`. |
| `moonmining.py:1205,1209,1217` | `SetTowerNotifications(towerID, showInCalendar, sendFuelNotifications)`; `SetTowerPassword(towerID, password, allowCorp, allowAlliance)`. Empty password is sent as `False` when editing the force field on a different item (`moonmining.py:1195-1217`). |
| `parklife/playerOwned.py:173-180` | Also calls the two-argument form `SetTowerPassword(towerID, password)`; omitted flags must retain their prior values. |
| `moonmining.py:1230-1238` | `GetMoonForTower(towerID)` is indexed as `(moonID, profileOrNone)`; a non-null profile is an iterable of `(typeID, abundance)` pairs. |
| `moonmining.py:1271-1275,1316` | `LinkResourceForTower(towerID, [(fromItemID, toItemID, typeID), ...])` returns a truth value; `ChangeStructureProvisionType(towerID, itemID, chosenOrNone)` has no result consumed. |
| `moonmining.py:1342-1357` | `RunMoonProcessCycleforTower(towerID)` returns no value; with Shift, `GetMoonProcessInfoForTower(towerID)` is iterated as six-field records. `OnMoonProcessChange(towerID, structures)` refreshes the production view. |
| `environment/invCache.py:1272-1275,1312-1314` | The silo capacity fallback calls `GetSiloCapacityByItemID(itemID)` and expects a numeric capacity. |
| `parklife/playerOwned.py:193-202` | `AnchorStructure(itemID, position)` is issued for types with the anchor-drop-for-structures effect. The menu's unanchor path activates the Dogma anchor-lift-for-structures effect (`menusvc.py:2469-2487`). |
| `moonmining.py:360-365` | `GetControlTowers()` supplies rows used for the corporation's other towers list. |

## EveJS comparison

EveJS v0.12.9 was traced as the behavioral reference. Its `posMgrService.js` formats sentry rows (`391-405`), notification settings (`415-427`), password calls including optional flags (`430-447`), permission tuple/rowset (`450-472`), and production RPCs (`586-615`). Its orbital handlers return no client value and refresh the orbital scene on accepted transitions (`617-660`). The state transitions live in `planetOrbitalState.js:1195-1237`.

Starbase settings use normalized, owner-scoped configuration in `starbaseAccessRuntime.js:27-54,71-107`. Deployment permissions are actually consulted by later operations; level 1 applies to the owning corporation, level 2 permits alliance members, and level 3 requires the Starbase Fuel Technician role (`starbaseAccessRuntime.js:296-311`). This implementation enforces the saved tower `anchor`, `unanchor`, `online`, and `offline` flags in the matching control-tower state transitions. The follow-up below adds module-level `viewput` and `viewputtake` checks to the POS silo inventory path. Usage enforcement for maintenance, reprocessing, compression, and jump-bridge paths is still open; `use` is a separate operation and is hidden for silos by the V24 client (`moonmining.py:2387-2397).

EveJS has a POS production runtime for modules that have a verified `controlTowerID` association. It validates provision choices, links and cycles and persists their state (`starbaseProductionRuntime.js:141-210`). It explicitly refuses to invent historical recipes or moon abundance; its process-info RPC also fails closed because the V24 payload is not verified (`starbaseProductionRuntime.js:335-347`). Its `GetMoonForTower` returns `[moonID, profile-or-null]` and never substitutes current moon ore fractions (`starbaseProductionRuntime.js:274-282`).

For the shared reference check, a direct EveJS `Handle_GetConsumptionFactor([123456789], {})` invocation returned `1.0`, matching the Host RPC result. The EveJS repository has no `server/package.json` test script or matching POS test file, so no EveJS end-to-end scenario was run.

## eve.rs implementation path

`CoreServices` constructs `PosMgrService` (`dotnet/src/Eve.Services.Core/CoreServices.cs:67`). `HostComposition` dispatches the three orbital methods to the registered `PlanetMgrService` and other POS calls to `SpaceContainers.PosMgrControlTowerOperationAsync` (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:447-456`). `SpaceComposition` injects the live character sessions and wires the tower callbacks and scheduled tower transitions (`dotnet/src/Eve.Host/Composition/SpaceComposition.cs:503-506,561-568`). The registered RPC methods and bound-object handling are in `dotnet/src/Eve.Services.Core/PosMgrService.cs:23-154`.

Control-tower settings are persisted in the existing deployed-tower row using `tower_config jsonb` (`dotnet/src/Eve.Ships/Migrations/0028_control_tower_posmgr_config.sql:1`, `PgDeployedStore.cs:97-115`). Orbital transitions have a separate durable state table (`dotnet/src/Eve.Planets/Migrations/0006_orbital_states.sql:1-9`) and are updated with inventory under one transaction, scheduled for completion, presented to the scene, and followed by item notifications (`dotnet/src/Eve.Services.Planets/PosMgrOrbitals.cs:39-171,229-260`).

Tower RPC handlers and their wire contracts are in `dotnet/src/Eve.Services.Space/SpaceContainers.PosMgr.cs:51-269`; database reads and writes lock the existing control-tower row and commit before sending the moon-process notification (`:285-319`). The end-to-end test starts a real loopback Host socket, registers `posMgr` with the live `SpaceContainers` provider, calls through Macho wire, uses PostgreSQL and the pinned SDE, checks configuration after rebuilding the service, exercises invalid input and verifies the wire notification, observer notification, and deployment permission enforcement (`dotnet/tests/Eve.Services.Space.Tests/ControlTowerHostTests.cs`). The separate orbital test exercises the registered Host wire against a real inventory item and database (`dotnet/tests/Eve.Host.Tests/PosMgrOrbitalHostTests.cs:37-80`); `PosMgrClientRpcHostWireTests.cs:11-58` separately checks registration/argument dispatch for every POS method with a capture callback.

## Coverage result and known limits

The initial implementation filled all 18 formerly missing CSV rows with evidence-backed statuses. The follow-up audit first closed five of the 13 partial rows whose behavior already matched the client and EveJS boundary. This implementation follow-up adds durable POS module-to-tower associations and closes three more rows with DB-backed registered-Host tests. The audited `posMgr` coverage now has 15 `IMPLEMENTED-TESTED` rows and five `PARTIAL-TESTED` rows, including both `GetControlTowers` callsites.

The pinned PostgreSQL SDE has `sde.mapMoons` and `sde.controlTowerResources`, but no moon-resource abundance. It does contain official reaction blueprint activities (120 total; 49 consume group 427 Moon Materials, and 41 produce group 428 Intermediate Materials). The EveJS POS production profile is explicitly `unconfigured`. Therefore a tower's moon response includes the moon ID and `None`; no moon abundance is fabricated. The Host now persists POS module deployment, anchor state, and explicit control-tower association. `ChangeStructureProvisionType` persists validated silo selections and permits clearing an unconfigured moon harvester; selected moon harvesting and reactions still fail closed. `LinkResourceForTower` now validates and persists associated silo links, rejects type mismatch/cycles, and sends `OnMoonProcessChange` after commit. `GetMoonProcessInfoForTower` fails closed because the V24 payload is unverified. `RunMoonProcessCycleforTower` requires GM authority; without links it reports that no cycle ran, while a configured cycle still lacks module onlining/resource accounting and an authoritative production runner.

Tower sentry values and password/force-field flags are stored and round-trip through their V24 shapes, but the Host has no starbase sentry controller or force-field admission system to consume them. The notification flags are different: the full EveJS server stores and returns them as UI preferences and has no downstream calendar or fuel-notification consumer, so missing delivery behavior is not a parity gap. The follow-up audit below reclassifies those notification RPC rows accordingly.

## Checks

Verified in this worktree:

- Release build of `dotnet/src/Eve.Host/Eve.Host.csproj` with `--no-restore -warnaserror` — succeeded, 0 warnings, 0 errors; this compiled all POS-touched source projects through Host.
- Release builds of `dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj` and `dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj` with `--no-restore -warnaserror` — each succeeded with 0 warnings and 0 errors.
- Release build of `dotnet/tests/Eve.Services.Core.Tests/Eve.Services.Core.Tests.csproj` with `-warnaserror` — succeeded with 0 warnings and 0 errors after restoring its missing NuGet assets.
- The complete `Eve.Host.Tests.HostServerTests` class on `eve_p3`, including its POS Host-wire/orbital cases — 484 passed, 0 failed, 0 skipped.
- The complete `Eve.Services.Space.Tests.ControlTowerHostTests` class on `eve_p3` — 6 passed, 0 failed, 0 skipped.
- `Eve.Services.Core.Tests.PosMgrServiceTests` and `TargetedParityRpcTests` — 8 passed, 0 failed, 0 skipped.
- Planet launch-placement, launch-quantity, and owner-concurrency Host cases that instantiate and register `PlanetMgrService` — 3 passed, 0 failed, 0 skipped. An initial filter used source filenames and matched no tests; the corrected filter ran the three named cases.
- Direct EveJS `GetConsumptionFactor` invocation — returned `1.0`.
- The first full Host test attempt stopped on a missing `sde_mission.skinrComponentPointValues` table in `eve_p3`. The missing schema was copied locally from the checked `eve_p4` mission SDE build 3405148 (`sde_mission._build`); the subsequent full Host test class passed. The `sde` schema in `eve_p3` is the V24.01 static build 3396210.
- Revalidation on 2026-11-13: Release builds of `Eve.Host`, `Eve.Host.Tests`, `Eve.Services.Space.Tests`, and `Eve.Services.Core.Tests` all succeeded with 0 warnings and 0 errors. The initial `--no-restore` build could not start because this checkout had no NuGet assets; restoring the four projects resolved that setup issue.
- Revalidation on `eve_p3`: `HostServerTests` — 484 passed; `ControlTowerHostTests` — 6 passed; `PosMgrServiceTests` plus `TargetedParityRpcTests` — 8 passed. All had 0 failed and 0 skipped.
- The POS implementation was already present on `main` at `362b4a0b5e36ad5dabacc8557d28627d2abac7b9`. CI run 1765 (`43397873007`) completed successfully and its Required CI gate passed. The separate latest-stable Rust clippy advisory job failed on `clippy::needless_bool` at `crates/eve-behaviors/src/actions/group.rs:419`, outside this POS change.
- No graphical V24 client was launched, and no full EveJS POS end-to-end scenario was run. EveJS code paths and the direct `GetConsumptionFactor` handler were checked; the remaining limitations above reflect unavailable legacy POS module and recipe authority, not verified graphical-client behavior.

## Tooling note

The MCP code-search tool requested in the repository workflow was not available in this session. The repository, client checkout, EveJS checkout, pinned SDE, and database were inspected directly with git, `rg`, source reads, PostgreSQL queries, and an EveJS method invocation.

## Follow-up audit of the 13 PARTIAL-TESTED rows (2026-11-13)

Each row was rechecked against V24.01 callsites across `/home/user/eve-logic`, the EveJS implementation in `/home/user/evejs-261108.1/server/src`, and the pinned SDE. The resulting row-by-row evidence is in [`posmgr-partial-audit-2026-11-13.md`](posmgr-partial-audit-2026-11-13.md). Five rows are closed as `IMPLEMENTED-TESTED`: `GetMoonForTower`, `GetMoonProcessInfoForTower`, `GetTowerNotificationSettings`, and both `SetTowerNotifications` callsites. Their current client-visible behavior is complete for the data and policy EveJS exposes: moon abundance is deliberately `None` when no verified profile exists, process info deliberately raises the payload-unverified notification, and notification flags are persisted preferences with no downstream EveJS consumer. The database-backed registered-service test checks their wire values and persistence.

The audit initially left eight rows `PARTIAL-TESTED`: `ChangeStructureProvisionType`, both starbase-permission calls, `GetTowerSentrySettings`, `SetTowerSentrySettings`, `LinkResourceForTower`, `RunMoonProcessCycleforTower`, and `SetTowerPassword`. This implementation follow-up promotes `ChangeStructureProvisionType`, `GetStarbasePermissions`, and the supported silo-link behavior in `LinkResourceForTower` after registered Host/DB tests. Five rows remain partial: `GetTowerSentrySettings`, `RunMoonProcessCycleforTower`, `SetStarbasePermissions`, `SetTowerPassword`, and `SetTowerSentrySettings`. The remaining gaps are module usage enforcement in POS container operations, a battery controller, force-field harmonic/admission state, and a production-cycle runner. The official SDE contains reaction blueprints, but EveJS's pinned POS legacy reaction and moon authority configuration is explicitly unconfigured; the SDE also has no historical moon abundance field. Those missing authorities are the reason to keep the dependent paths fail-closed.

One narrow production behavior was corrected in this follow-up. EveJS's GM cycle path refuses a force cycle with no configured links (`starbaseProductionCycles.js:196-197`); its `requireProductionResult` omits the `STARBASE_PRODUCTION_LINK_REQUIRED` message and consequently returns a misleading generic access notification (`starbaseProductionRuntime.js:341-364`). eve.rs now checks the persisted graph after the GM check and reports that no production links are configured and no cycle ran. This message correction is an intentional deviation from that EveJS fallback text; the no-links refusal and GM gate are preserved. A connected graph still fails closed pending the module and production runtime.

This follow-up used the `eve_p3` database. The earlier focused tests first failed on the old no-links message and shorter process-info message before those corrections were made. In the module follow-up, the new DB-backed host-wire scenario first failed because the host could not launch a POS silo, then exposed the harvester-clear mismatch and duplicate usage-row acceptance; all three were corrected without reducing assertions. The final focused result and class/build totals are recorded in this file's final checks section.

## POS module follow-up checks (2026-11-13)

The V24 client callsites were rechecked across the full `/home/user/eve-logic` checkout. The affected paths are `moonmining.py:704-743` for module rows and permission saves, `moonmining.py:1271-1316` for linked resource and provision configuration, `moonmining.py:541-562,1128-1137` for sentry settings, `moonmining.py:1342-1357` for forced cycles/process info, and `playerOwned.py:173-180` plus `moonmining.py:1195-1219` for the password calls. Whole-checkout searches found no alternate POS inventory operation or force-field harmonic implementation in eve.rs.

EveJS evidence: `starbaseModuleRuntime.js:190-207,283-370,390-440,490-518` owns module association, online eligibility, and anchoring/online transitions; `starbaseProductionRuntime.js:141-210` owns provision/link validation; `starbaseAccessRuntime.js:173-181,296-311` returns and enforces usage rows; `starbaseDefenseController.js:149-152` consumes sentry settings; `starbaseAccessRuntime.js:162-170,216-263` configures force-field access and checks ship password credentials. The pinned `config/pos-legacy-production.json` remains unconfigured for legacy reactions and moon resources.

Verified in this implementation follow-up:

- The focused registered Host/DB flow `ControlTowerHostTests::PosMgr_anchors_POS_modules_and_roundtrips_module_permissions_and_production_links_over_host_wire` passed on `eve_p3`, 1 passed, 0 failed, 0 skipped. It anchors two silos and one moon harvester to an anchored tower, validates and clears provisions, verifies the tower/module permission rowset and exact saved values, rejects duplicate permission rows and invalid production links without mutation, persists a valid silo link, and checks the `OnMoonProcessChange` Host wire arguments.
- The test first failed on the expected gaps before fixes: module launch initially had no authoritative Host state; moon-harvester `None` selection returned an error despite EveJS permitting the clear; duplicate usage rows were silently collapsed instead of rejected. Assertions were retained and the implementation was corrected.
- The complete `ControlTowerHostTests` class passed on `eve_p3`: 7 passed, 0 failed, 0 skipped. Its module test concurrently updates two silo provisions over separate registered Host connections, retries one update, and verifies both durable values. A targeted Host persistence slice across the direct `PgDeployedStore` user classes passed 39, failed 0, and skipped 21 because `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE` were not configured. Skips are not counted as passes.
- Release builds of `Eve.Ships`, `Eve.Services.Space`, and `Eve.Services.Space.Tests` passed with warnings treated as errors: 0 warnings and 0 errors. Rust verification passed: `cargo fmt -p eve-space`, `cargo check -p eve-space`, `cargo clippy -p eve-space --all-targets -- -D warnings`, and the focused control-tower deployable test (1 passed).
- No graphical V24 client was launched and no full EveJS POS end-to-end flow was run. EveJS handlers and full-client callsites were checked in source; the remaining five coverage rows stay open for the downstream behavior above.

## POS inventory permission follow-up (2026-11-13)

### Client and EveJS check

The V24 client lists each deployed starbase module in the permissions editor and sends the mutable `viewput`, `viewputtake`, and `use` fields (`/home/user/eve-logic/eve/client/script/ui/inflight/moonmining.py:704-743,2387-2397`). The client hides `use` except for ship maintenance, reprocessing, and compression arrays. It offers storing an assembled ship in a maintenance array through `menusvc.py:1642-1643,1856-1857,2863-2884`, and jump-bridge travel through `menusvc.py:2095-2106` and `parklife/autopilot.py:349,377`. The silo inventory uses its `maxOperationalDistance` and capacity data, with `GetSiloCapacityByItemID` as its fallback (`/home/user/eve-logic/eve/client/script/environment/invCache.py:1264-1292`).

EveJS enforces the permission levels and owner-role bypass in `/home/user/evejs-261108.1/server/src/services/starbase/starbaseAccessRuntime.js:296-311`. Its inventory boundary separately validates POS association, anchored/offline state, visibility, and type range (`/home/user/evejs-261108.1/server/src/services/starbase/starbaseInventoryRuntime.js:73-110`); taking permission implies viewing/depositing (`starbaseInventoryRuntime.js:116-120`); maintenance inventory operations check view/deposit/take independently (`starbaseInventoryRuntime.js:178-184`); and production inventory access plus silo provision validation are in `starbaseProductionRuntime.js:213-254`. It also checks module/tower health and reinforcement for production access (`starbaseProductionRuntime.js:213-223`), uses the `use` flag for ship maintenance and bridge travel (`/home/user/evejs-261108.1/server/src/services/ship/ejectRuntime.js:259`, `/home/user/evejs-261108.1/server/src/services/starbase/starbaseTravelRuntime.js:75`), and has a separate POS processing inventory path (`/home/user/evejs-261108.1/server/src/services/starbase/starbaseProcessingInventory.js:24-36,124`).

### Implementation and regression

`ISpaceContainers` now exposes view and deposit authorization separately from loot rights. The registered `invbroker` Host path recognizes an anchored, associated POS silo while it is offline; it reads the owning tower's persisted permission row, applies corporation/alliance/caretaker and CEO/director/config-role rules, checks the silo's operational distance, and rechecks access through retained inventory bindings. The existing inventory move transaction still owns item custody, volume capacity, split behavior, and notifications. Silo deposits must match the persisted provision type and the SDE cargo-group rule.

`ControlTowerHostTests::PosMgr_saved_usage_permissions_control_POS_inventory_over_registered_host_wire` is a PostgreSQL-backed test on `eve_p3`. It registers both `posMgr` and `invbroker` on a real Host socket, anchors a silo to its control tower, persists permissions and a provision, and calls the resulting bound inventory. It verifies offline-only access, V24 contents rowset shape, allowed corp view/deposit, rejected wrong-provision deposit and denied single/batch take with no mutation or notification, then caretaker `viewputtake` take success, persisted custody/location/quantity, and `OnItemChange` / `OnItemsChanged` / cargo event wire notification order. The first run failed at the registered Host `GetInventoryFromId(silo)` call because `SpaceContainers.OpenAsync` did not recognize POS modules; the final focused regression passed without weakening assertions.

RPC-02158 remains `PARTIAL-TESTED`: the covered silo path now consumes `viewput` and `viewputtake`, but the client exposes `use` for other POS inventory types and eve.rs has no corresponding ship-maintenance/reprocessing/compression-array or jump-bridge consumer. The Host also has no POS module health/incapacitation or reinforcement state for the EveJS production-availability checks. Those remaining behaviors are not represented by the silo E2E, so the CSV row is not promoted.

### Remaining rows rechecked against V24 and EveJS

| Coverage row | Client evidence | EveJS consumer | Decision and remaining work |
| --- | --- | --- | --- |
| RPC-02154 `GetTowerSentrySettings`; RPC-02162 `SetTowerSentrySettings` | V24 reads the five sentry fields and sends the five setting values in `moonmining.py:541-562,1128-1137`. | `starbaseDefenseController.js:149-152` reads them for automated POS battery target selection. | Both stay partial. Their row wire shape and persistence are tested, but eve.rs has no associated battery defense controller to consume the standing, security, status-drop, war, or alliance-standing policy. |
| RPC-02157 `RunMoonProcessCycleforTower` | `moonmining.py:1342-1357` issues the cycle and refreshes the production view; Shift then requests six-field process rows. | `starbaseProductionCycles.js:188-220` runs a persisted graph with an authoritative recipe/resource provider. `starbaseProductionAuthority.js:97-102` fails closed when the configured legacy source is unavailable. | Stays partial. The no-links/GM refusal is tested, but there is no module onlining/resource-accounting cycle runner. The pinned EveJS legacy authority has empty reaction and moon data, and the SDE has no historical moon abundance; importing modern moon fractions or fabricated cycle results would be incorrect. |
| RPC-02158 `SetStarbasePermissions` | The V24 editor exposes all three flags per associated module; for silos, `use` is hidden (`moonmining.py:2387-2397`). | `starbaseAccessRuntime.js:296-311`, `starbaseInventoryRuntime.js:116-120,178-184`, and `starbaseProductionRuntime.js:213-254`. | Silo view/deposit/take now have an E2E consumer and remain tested. The row stays partial because the other visible `use` consumers and corresponding POS arrays are not implemented or exercised. |
| RPC-02161 `SetTowerPassword` | V24 compares ship harmonic with the control tower field (`playerOwned.py:91-115`), opens tower and ship password prompts (`playerOwned.py:173-191`, `menusvc.py:3173`), and saves the force-field settings (`moonmining.py:1195-1219`). | `starbaseAccessRuntime.js:162-170,216-263` derives tower admission and validates ship credentials; `starbaseForceFieldBoundary.js:86-223` enforces field crossing and harmonics. | Stays partial. eve.rs stores a salted password hash and flags, but the simulator protocol/SpaceSessions path has no client-visible force-field harmonic or boundary-admission consumer to validate the saved credentials. |

The remaining open rows and these decisions were checked against the full local V24 client and pinned EveJS source. No graphical client or complete EveJS POS scenario was run.

### Verification after inventory permission follow-up

- The focused `PosMgr_saved_usage_permissions_control_POS_inventory_over_registered_host_wire` PostgreSQL test passed on `eve_p3`: 1 passed, 0 failed, 0 skipped. Its pre-implementation run failed at the registered `invbroker.GetInventoryFromId(silo)` call, as expected.
- The selected `SpaceContainerTests`, `AnalysisBeaconHostTests`, and `ControlTowerHostTests` classes passed on `eve_p3`: 51 passed, 0 failed, 0 skipped. The complete `ControlTowerHostTests` class accounts for 8 passes.
- The complete `Eve.Host.Tests.HostServerTests` partial class passed on `eve_p3`: 491 passed, 0 failed, 0 skipped (5m44s). `CustomsOfficeHostTests` passed separately: 3 passed, 0 failed, 0 skipped.
- The focused inventory classes passed: `InvBrokerTests` plus `InventoryWireTests` — 48 passed; `InventoryRpcFixTests` plus `StructureControlInfoTests` — 27 passed. No tests were skipped in either run.
- Release builds with `-warnaserror` succeeded with 0 warnings and 0 errors for `Eve.Services.Ships`, `Eve.Services.Inventory`, `Eve.Services.Space`, and `Eve.Services.Space.Tests`.
- One preliminary Host test filter used `FullyQualifiedName~InvBrokerInventoryHostWireTests`; the tests are part of the partial `HostServerTests` class, so that filter matched no tests. It is not counted as a pass; the complete `HostServerTests` class was run and passed afterward.
- No graphical V24 client or full EveJS POS scenario was run. The client and EveJS source paths listed above were inspected directly.
