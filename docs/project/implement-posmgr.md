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

Starbase settings use normalized, owner-scoped configuration in `starbaseAccessRuntime.js:27-54,71-107`. Deployment permissions are actually consulted by later operations; level 1 applies to the owning corporation and level 2 also permits a verified alliance member (`starbaseAccessRuntime.js:296-311`). This implementation now enforces the saved tower `anchor`, `unanchor`, `online`, and `offline` flags in the matching control-tower state transitions. Owner CEO/director/config-role users retain their existing bypass. Module-level `viewput`, `viewputtake`, and `use` enforcement requires POS module ownership/association, which eve.rs does not yet model.

EveJS has a POS production runtime for modules that have a verified `controlTowerID` association. It validates provision choices, links and cycles and persists their state (`starbaseProductionRuntime.js:141-210`). It explicitly refuses to invent historical recipes or moon abundance; its process-info RPC also fails closed because the V24 payload is not verified (`starbaseProductionRuntime.js:335-347`). Its `GetMoonForTower` returns `[moonID, profile-or-null]` and never substitutes current moon ore fractions (`starbaseProductionRuntime.js:274-282`).

For the shared reference check, a direct EveJS `Handle_GetConsumptionFactor([123456789], {})` invocation returned `1.0`, matching the Host RPC result. The EveJS repository has no `server/package.json` test script or matching POS test file, so no EveJS end-to-end scenario was run.

## eve.rs implementation path

`CoreServices` constructs `PosMgrService` (`dotnet/src/Eve.Services.Core/CoreServices.cs:67`). `HostComposition` dispatches the three orbital methods to the registered `PlanetMgrService` and other POS calls to `SpaceContainers.PosMgrControlTowerOperationAsync` (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:447-456`). `SpaceComposition` injects the live character sessions and wires the tower callbacks and scheduled tower transitions (`dotnet/src/Eve.Host/Composition/SpaceComposition.cs:503-506,561-568`). The registered RPC methods and bound-object handling are in `dotnet/src/Eve.Services.Core/PosMgrService.cs:23-154`.

Control-tower settings are persisted in the existing deployed-tower row using `tower_config jsonb` (`dotnet/src/Eve.Ships/Migrations/0028_control_tower_posmgr_config.sql:1`, `PgDeployedStore.cs:97-115`). Orbital transitions have a separate durable state table (`dotnet/src/Eve.Planets/Migrations/0006_orbital_states.sql:1-9`) and are updated with inventory under one transaction, scheduled for completion, presented to the scene, and followed by item notifications (`dotnet/src/Eve.Services.Planets/PosMgrOrbitals.cs:39-171,229-260`).

Tower RPC handlers and their wire contracts are in `dotnet/src/Eve.Services.Space/SpaceContainers.PosMgr.cs:51-269`; database reads and writes lock the existing control-tower row and commit before sending the moon-process notification (`:285-319`). The end-to-end test invokes the registered `posMgr` service, round-trips the marshalled values, uses PostgreSQL and the pinned SDE, checks configuration after rebuilding the service, exercises invalid input and verifies observer notifications and deployment permission enforcement (`dotnet/tests/Eve.Services.Space.Tests/ControlTowerHostTests.cs:157-359`). The separate orbital test exercises the registered Host wire against a real inventory item and database (`dotnet/tests/Eve.Host.Tests/PosMgrOrbitalHostTests.cs:37-80`); RPC registration/argument dispatch for every newly added method is covered by `PosMgrClientRpcHostWireTests.cs:11-58`.

## Coverage result and known limits

All 18 formerly missing CSV rows now have an evidence-backed status. Five distinct methods are `IMPLEMENTED-TESTED`: `AnchorOrbital`, `GetConsumptionFactor`, `GetSiloCapacityByItemID`, `OnlineOrbital`, and `UnanchorOrbital`. The other 13 rows are `PARTIAL-TESTED` and state their limit in the CSV. The pre-existing `GetControlTowers` duplicate rows remain implemented.

The pinned PostgreSQL SDE has `sde.mapMoons` and `sde.controlTowerResources`, but no moon-resource abundance or legacy reaction tables. Therefore a tower's moon response includes the moon ID and `None`; no process graph, recipe, or production output is fabricated. The current Host also has no POS module deployment/association state. Consequently `ChangeStructureProvisionType` refuses a purported module association, and `LinkResourceForTower` validates its wire input and durably clears an empty graph with a post-commit notification but refuses nonempty links. `GetMoonProcessInfoForTower` and GM-only `RunMoonProcessCycleforTower` fail closed in line with EveJS's unavailable authority/payload behavior.

Tower sentry values, password/force-field flags, and notification preferences are stored and round-trip through their V24 shapes. The Host does not currently consume these settings in an NPC starbase-aggression controller, force-field admission system, calendar event, or fuel-notification preference. In particular, the existing fuel-item notification subscriber is independent of `sendFuelNotifications`. These calls are marked partial instead of claiming full gameplay effects.

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
