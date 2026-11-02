# Deep correctness handoff: wars, sovereignty and factional warfare

## Scope and current status

This pass traced the V24.01 structure war tooltip from its client call through the registered Host service and persisted war relations. It fixed one confirmed wire-contract defect. The sovereignty and factional-warfare handoffs already document tested Host paths; this pass did not change those services. The remaining war negotiation provider is still open in `docs/project/TODO.md`.

## Confirmed defect and fix: public war information

- The only V24.01 client RPC call is `eve/client/script/ui/structure/structureIcon.py:44`: `GetPublicWarInfo(self.wars)`. The same caller treats the reply as a collection: it iterates all records to prime both owners, renders the first five, and computes the remainder count (`structureIcon.py:50-65`).
- The input is sourced from `structureInfo.wars` by `structureEntryController.GetWars` (`eve/client/script/ui/structure/structureBrowser/controllers/structureEntryController.py:195-196`). The deployment UI also computes `len(structureInfo.wars)` (`eve/client/script/ui/services/structure/structureDeployment.py:58-60`). Structure list entries pass this value into `StructureIcon` (`structureBrowser/entries/structureEntry.py:49-50` and `structureEntryMyCorp.py:157-158`).
- Host registration constructs `WarsInfoMgrService` with the shared `RelationStore` in `dotnet/src/Eve.Host/Rpc/CorporationRpc.cs:70-76`. The service previously parsed argument zero as one scalar war ID and returned a single KeyVal or None (`dotnet/src/Eve.Services.Corporations/WarsInfoMgrService.cs:87-100`). EveJS 261028.1 follows the same single-ID behavior (`server/src/services/corporation/warsInfoMgrService.js:129-133`).
- The service now reads requested IDs from the client collection, selects persisted war relations, and returns a PyList in request order. Missing IDs are omitted; an empty input returns an empty PyList. No state changes or notifications belong to this read RPC.
- Regression: `Wars_info_rpcs_keep_client_shapes_through_registered_host_with_transport_kwargs` sends multiple IDs, an unknown ID, and an empty list through Host with transport kwargs, and checks the list shape and returned war IDs. Before the fix it failed because Host returned PyNone. The test is PostgreSQL-backed and uses `eve_p4`.

This is an intentional compatibility difference from EveJS: the V24.01 caller and downstream consumers require a collection, so the client contract takes precedence over EveJS's scalar handler. No gameplay mechanic or SDE-derived value changed.

## Related area evidence reviewed

- Sovereignty's prior cx4 handoff records the V24.01 caller/consumer trace and the Host/PostgreSQL test for claim and hub reads, fuel ACL mutation, and Skyhook persistence: `docs/project/service-audit/handoff-sovMgr-coverage-cx4.md`. No sovereignty code was changed in this pass.
- The faction-warfare audit describes whole-client `facWarMgr` callsites and payload consumers, including character/corporation enlistment, donation tax retries, rank, stats, and LP pool reads: `docs/project/service-audit/DEEPCHECK_facWarMgr.md`. Existing FW design documents the shared `FwStore` and transaction paths. No additional FW defect was established in this pass, so no FW code was changed.
- `warRegistry` negotiation reads and writes remain open: Host currently lacks the persisted negotiation state and client notification flow; see `docs/project/TODO.md:96` and `docs/project/service-audit/DEEPCHECK_wars.md`. This work does not claim that flow is complete.

## Checks

- Before-fix reproduction: focused Host wire test failed as expected: the client collection input produced PyNone.
- After-fix `eve_p4` tests: `Wars_info_rpcs_keep_client_shapes_through_registered_host_with_transport_kwargs` passed (1 passed, 0 failed, 0 skipped); the related `WarsInfoMgrServiceTests` class passed (5 passed, 0 failed, 0 skipped).
- After fetching and merging `origin/main` (already up to date), the same Host and Corporation tests were rerun and passed (1 and 5 passed, no failures or skips).
- `dotnet build` succeeded for `Eve.Services.Corporations`, `Eve.Corporations.Tests`, and `Eve.Host.Tests`, with 0 warnings and 0 errors.
- The graphical V24.01 client was not used in this pass.
- Push and Actions status will be appended after the commit and CI gate complete.
