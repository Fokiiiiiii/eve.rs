# Host RPC coverage handoff: RPC-00950 through RPC-01328

## Scope findings

- RPC-00950 is not a remote call: `corpBillsAutoPaySettingsPanel.py` line 59 calls `automaticPaymentSettings.get(...)` on the dictionary returned by `GetAutomaticPaySettings`. The remote method is `billMgr.GetAutomaticPaySettings` (same file, lines 36 and 83). Host composition registers `BillMgrService` in `dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:71`; `BillMgrService.HasMethod` lists the actual methods in `dotnet/src/Eve.Services.Corporations/BillMgrService.cs:31-37`. EveJS implements the settings endpoint at `server/src/services/account/billMgrService.js:202-230`.
- RPC-00967 and RPC-00968 are not remote calls: `bountySvc.py:50,84` call the local `Memoize.clear_memoized` helper on `GetMyBounties`. The actual remote calls nearby are `bountyProxy.GetMyBounties` and `bountyProxy.AddToBounty` (`bountySvc.py:82-85`). No extra Host route belongs here.
- Blueprint client flow: `blueprintSvc.py:25` reads limits; lines 96-108 load a blueprint by item and turn the reply into a BlueprintInstance; lines 137-157 request owner and optional facility lists; lines 167-195 attach facility/location data before UI consumers receive them. EveJS counterparts: `server/src/services/industry/blueprintManagerService.js:24-53`.
- Bounty client flow: `bountySvc.py:82-85` places a bounty and clears the local memoized contribution list; lines 147-149 fetch bounty rows; 218-229 consume hunter rankings and my-contribution rows; 232-242 issue GM methods; 256-290 sell/cancel and load kill rights. The returned values feed the bounty service caches, kill-right tracker, and UI-facing methods in this same file. EveJS counterparts: `server/src/services/bounty/bountyProxyService.js:378-525` and `527-554`; sale state is in `server/src/services/bounty/killRightState.js:520-569`.
- Skyhook client flow: `sovereigntyResourceSvc.py:263-268` returns the corporation list through the remote service and exposes the result through a five-second memoized wrapper. EveJS counterpart: `server/src/services/sovereignty/colonyResourcesMgrService.js:108-121`.
- Crate client flow: `lootItem.py:54-55` claims an item to the chosen inventory destination; `lootItem.py:102-104` claims a fitting; `controller.py:113` and 299 open a stack and consume the returned (opened, remaining-request) counts. EveJS counterparts: `server/src/services/campaign/crateService.js:603-704`.

## Host wiring and regression coverage

Host composition currently registers these implementations:
- `billMgr`: `dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:71`
- `blueprintManager`: `dotnet/src/Eve.Host/Rpc/IndustryRpc.cs:59`
- `bountyProxy`: `dotnet/src/Eve.Host/Rpc/CrimewatchRpc.cs:119-124`, backed by `PlayerBountyRpc.HandleAsync` and the crimewatch store.
- `crateService`: `dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:51`, backed by `PersistenceCrateServiceBackend`.
- `colonyResourcesMgr`: `dotnet/src/Eve.Host/Composition/SovereigntyComposition.cs:251`.

Existing registered-Host wire tests cover blueprint and corporation skyhook reads in `dotnet/tests/Eve.Host.Tests/BlueprintAndSkyhookHostWireTests.cs`, crate claims and invalid requests in `dotnet/tests/Eve.Host.Tests/CrateServiceHostWireTests.cs`, and bill-manager list/payment shapes in `dotnet/tests/Eve.Host.Tests/BillMgrHostRpcTests.cs`. The bounty Host wire regression is in `dotnet/tests/Eve.Host.Tests/BountyProxyHostWireTests.cs`.

## Remaining work

The Bounty and BillMgr Host wire tests pass locally. Selected Actions run 40279626679 passed for the Bounty, Blueprint/Skyhook, Crate, and BillMgr Host wire filters at commit 0d21ff3e; it reported no failed tests or jobs. The push-triggered ci.yml run was still queued when checked. This batch did not use PostgreSQL or the graphical client. Do not classify the three local helper calls above as missing server routes.

## Current verification

- Host route regression: `Bounty_proxy_client_reads_use_the_registered_host_route_and_wire_shapes` passed (1 passed, 0 failed, 0 skipped). Its calls carry the transport-added `machoVersion` kwarg; both the populated bounty route and empty-state result shapes are checked through Host registration.
- Bill manager Host route regression: `Bill_manager_routes_empty_corporation_reads_and_refuses_unavailable_payments` passed (1 passed, 0 failed, 0 skipped). The test now carries `machoVersion` in its call kwargs.
- The local C# build and focused test commands completed; no PostgreSQL database was used.
- The blueprint/skyhook and crate Host tests were read and are already present; they were not rerun locally in this pass. GitHub Actions still needs the selected Host wire tests after push.
