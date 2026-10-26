# Host RPC handoff: 01687-01724

## Client callers and consumers

A whole-tree search of `/home/user/eve-logic` found:

- `carbon/client/script/sys/infoGatheringSvc.py:35-39` reads all six `GetStateAndConfig` fields. At lines 95-108 it sends event batches and treats the response as a local mapping: `ret.iteritems()` disables event types or the service. This is not an RPC named `iteritems`; no `infoGatheringMgr.iteritems` route is called.
- `eve/client/script/ui/shared/zhReport/reportSvc.py:35-50` is the sole callsite for the eight report methods. It passes 5 values for inappropriate language and 2 for each other method. The caller ignores the RPC result and returns its own “Report Success” string.
- `eve/client/script/ui/services/insurgencyCampaignSvc.py:51-60` consumes `(solarsystemID, snapshot-or-None)`, retries once if the session system changed, and caches the pair only when the returned ID matches. Lines 63-88 consume campaign notices and filter by covered systems. Lines 92-116 expose the all-campaign list to consumers.
- Snapshot consumers across the client include `insurgenceDashboardSvc.py:48-76,93-100`, `pirateinsurgency/client/dashboard/sidepanels/progress.py:57-76,105-113,132-133`, `fwwarzone/client/dashboard/warzoneMapPanel.py:407-409`, and the campaign selectors/filters in `eve/client/script/ui/shared/theatersOfWar/landingPage/landingPagePanel.py:118`, `eve/client/script/ui/shared/mapView/filters/mapFilterInsurgencies.py:27-29`, and `eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderPirateInsurgenceSystems.py:25-27`. These read the campaign state, origin, covered systems, faction, warzone, campaign ID, expiry, points, and structure fields.

## EveJS comparison and Host path

- EveJS v0.12.9 `server/src/services/support/infoGatheringMgrService.js:40-58` records a telemetry count in process memory and returns an empty status dictionary; its default config is disabled. Eve.rs returns the same client-visible empty statuses and disabled config.
- EveJS `server/src/services/support/ingamereportService.js:14-24,61-80,87-121` records eight report types in process memory and returns None. Eve.rs returns None through `CoreServices` registration, but the production composition does not provide a report recorder. Durable report retention remains open in `docs/project/TODO.md:1061`; this RPC coverage does not claim report storage.
- EveJS `server/src/services/map/insurgencySolarsystemService.js:14-46,67-87` returns the same Ex2 campaign snapshot fields, all-visible list, and local ID/snapshot-or-None pair. Eve.rs production registration is `dotnet/src/Eve.Host/Rpc/CoreRpc.cs:142`; `dotnet/src/Eve.Host/Composition/InsurgencyComposition.cs:56-74` reads visible campaign state from the persisted store and maps it to the wire snapshot. Existing service logic builds the Ex2 fields and local pair in `dotnet/src/Eve.Services.Core/FactionActivityRpcServices.cs:315-360`.

## Row coverage

- RPC-01687/01688: `LogInfoEventsFromClient` reply; exercised with a valid 12-column event row and transport `machoVersion`.
- RPC-01689: local `ret.iteritems()` usage, not a server method or route.
- RPC-01690-01705: all eight `ingamereport` methods; each is called through the Host socket and returns the client-ignored None result.
- RPC-01719/01720: all-visible campaign list; Host socket asserts a campaign Ex2 object.
- RPC-01721-01724: local ID/snapshot pair; Host socket asserts the matching system ID and snapshot.

`dotnet/tests/Eve.Host.Tests/SupportRpcWireTests.cs` exercises these calls through `HostServerTests.Start`, the service registry, dispatcher, and a real loopback socket. Campaign fixtures are test input; production reads persisted state. No database was used.

## Checks and remaining work

- The local Host test-project build was attempted twice with shared compilation disabled; both attempts stalled while Roslyn compiled `Eve.Host.Tests` and were interrupted. No local test passed.
- Focused GitHub Actions dispatch and push-gate checks are pending for the commit containing this handoff.
- No graphical-client run was made. Report recording/durable retention remains open as noted above.


## Follow-up CI compile fix

The first pushed revision's GitHub Actions logs identified five compile errors in the already-present `Rpc01790HostCoverageTests.cs`: missing `Eve.Services` import for `Py` and incorrect `TestFront.Packets` qualification for the top-level `Packets` type. The fix adds the import and calls `Packets.CallWithKwargs`. This was unrelated to the new support/insurgency test but blocked the shared Host test project.

The Host test-project build was retried locally after that fix; Roslyn stalled without output and was interrupted. No local compile or focused test passed. A fresh focused Actions run and push CI check are pending for the follow-up commit.

The follow-up push CI exposed one additional compile error in the same pre-existing Host test: `Packets.CallWithKwargs` builds a marshal value, while `TestFront.SendAsync` accepts a HostMessage. The test now wraps the encoded value in `new Packet(open.SessionId, MarshalCodec.Encode(...))`, matching the real Host socket test harness. Fresh Actions checks are pending.

The latest full Host shard on Actions identified two failures now addressed locally: the info-gathering KeyVal decodes with a dict in `PyObject.Args`, so the wire regression now compares the full expected KeyVal shape; the parallel Host coverage test now sends the session-open packet before making RPC calls. The focused `Support_and_insurgency` test on commit `c571d58b` failed at the outdated Args type assertion; it has not yet been rerun with the corrected assertion. Local test commands continue to stall while Roslyn compiles `Eve.Host.Tests` and were interrupted.
