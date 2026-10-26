# Host RPC handoff: 01687-01724

## Client callers and consumers

A whole-tree search of `/home/user/eve-logic` found:

- `carbon/client/script/sys/infoGatheringSvc.py:35-39` reads all six `GetStateAndConfig` fields. At lines 95-108 it sends event batches and treats the response as a local mapping: `ret.iteritems()` disables event types or the service. This is not an RPC named `iteritems`; no `infoGatheringMgr.iteritems` route is called.
- `eve/client/script/ui/shared/zhReport/reportSvc.py:35-50` is the sole caller of the eight report methods. Inappropriate language takes five arguments; the other methods take two. The caller ignores the RPC result and returns its own success string.
- `eve/client/script/ui/services/insurgencyCampaignSvc.py:51-60` consumes `(solarsystemID, snapshot-or-None)`, retries if the system changed, and caches only a matching ID. Lines 63-88 consume notices and filter by covered systems. Lines 92-116 expose the all-campaign list.
- Snapshot consumers include `eve/client/script/ui/services/insurgenceDashboardSvc.py:48-76,93-100`, `pirateinsurgency/client/dashboard/sidepanels/progress.py:57-76,105-113,132-133`, `fwwarzone/client/dashboard/warzoneMapPanel.py:407-409`, and selectors/filters at `eve/client/script/ui/shared/theatersOfWar/landingPage/landingPagePanel.py:118`, `eve/client/script/ui/shared/mapView/filters/mapFilterInsurgencies.py:27-29`, and `eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderPirateInsurgenceSystems.py:25-27`. They read campaign state, origin, covered systems, faction/warzone, campaign ID, expiry, points, and structure.

## EveJS comparison and Host path

- EveJS v0.12.9 `server/src/services/support/infoGatheringMgrService.js:40-58` records event count in process memory, returns empty statuses, and defaults to disabled config. Eve.rs returns the same client-visible empty statuses and disabled config.
- EveJS `server/src/services/support/ingamereportService.js:14-24,61-80,87-121` records eight report kinds in process memory and returns None. Eve.rs returns None through `CoreServices`, but production composition has no report recorder. Durable report retention remains open in `docs/project/TODO.md:1061`; this coverage does not claim report storage.
- EveJS `server/src/services/map/insurgencySolarsystemService.js:14-46,67-87` returns the Ex2 campaign snapshots, visible list, and local ID/snapshot-or-None pair. Eve.rs registers the service in `dotnet/src/Eve.Host/Rpc/CoreRpc.cs:142`; `dotnet/src/Eve.Host/Composition/InsurgencyComposition.cs:56-74` reads persisted visible campaign state, and `dotnet/src/Eve.Services.Core/FactionActivityRpcServices.cs:315-360` builds the wire fields.

## Row coverage

- RPC-01687/01688: valid 12-column `LogInfoEventsFromClient` row with transport `machoVersion`.
- RPC-01689: local `ret.iteritems()`, not a server method or route.
- RPC-01690-01705: all eight report routes, called through the Host socket; each returns the client-ignored None result.
- RPC-01719/01720: all-visible campaign list; Host socket asserts a campaign Ex2 object.
- RPC-01721-01724: local ID/snapshot pair; Host socket asserts matching system ID and snapshot.

`dotnet/tests/Eve.Host.Tests/SupportRpcWireTests.cs` exercises these calls through `HostServerTests.Start`, the real registry and dispatcher, and a loopback socket. Campaign fixtures are test input; production uses persisted state. No local database was used.

## Checks and remaining work

- Focused Actions test `Support_and_insurgency_rpcs_return_client_shapes_through_host_registration` passed on `da7278cf` (run 40279466798).
- Push CI for `da7278cf` passed .NET build, Rust checks, fixtures, Python, Windows, end-to-end, and most .NET shards. One unrelated inventory test failed because its expected route count was 1,423 while the registry contained 1,424. Current main updates the expected count to 1,424. Focused Actions verification against current main `5014f255` is pending (run 40281531643); push-gate run 40279458341 is still queued.
- Local .NET build and focused test commands stalled during Roslyn compilation and were interrupted. No local compile/test passed. No graphical-client run was made.
- Earlier shared Host compilation/assertion issues identified by Actions were fixed in `Rpc01790HostCoverageTests.cs` and `DungeonDynamicResourceHostWireTests.cs`; the latest Host shard 1 passed.
- Report recording/durable retention remains open as noted above.
