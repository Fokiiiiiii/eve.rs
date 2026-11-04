# Deep check: taleMgr

Source snapshot: main commit `7a3a02e9`; client: V24.01 eve-logic commit `1dbb5711a072a146e1d228365668ac05655eefe1`.

## Client call and reply flow

| Method | V24.01 caller and arguments | Reply/notification consumed |
|---|---|---|
| `get_active_tales_by_template` | `taleclient/tale_service.py:127-129`, one positional template ID; the map filter calls the local helper at `eve/client/script/ui/shared/mapView/filters/mapFilterVanguardNemesisConvoys.py:30-34` | The helper maps any returned dict’s `tale_id`, `template_id`, `solar_system_id`, `start_date`, and `end_date` to `ActiveTaleThin`. Both EveJS and Host return an empty list for this request. |
| `GetGlobalWorldEventTales` | `taleclient/tale_service.py:143-145`, no arguments; memoized 15 seconds by the client | The client flow uses the accompanying `OnTaleData(solarSystemID, data)` notification. `TaleService.OnTaleData` at :67-75 adds data for the current system; `IncursionSvc.OnTaleData` at `eve/client/script/ui/shared/incursionSvc.py:67-80` reads each tale KeyVal and starts the incursion UI. |

The client event consumers read tale IDs, template class/name, system location, state, influence, effects and system membership. No client UserError path was found. These are read calls; the notification is emitted before the response.

## Host and EveJS

The registered `taleMgr` service is composed in `dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:60-65`. `IncursionAnomalyService.cs:68-96,126-141` implements both methods. The global call returns an empty cached result with a 15-minute proxy cache and emits `OnTaleData`; a positive template ID also emits the notification and returns an empty list. Invalid template IDs return an empty list without a notification. The notification is built from the persisted incursion read model at :255-295. No mutation, persistence write, or post-call notification exists.

EveJS v0.12.9 matches the empty reply and notification order in `server/src/services/tale/taleMgrService.js:265-286`; its notification helper is at :218-239 and builds incursion KeyVals at :75-107. The four CSV rows RPC-02701–02704 were reclassified as IMPLEMENTED-UNTESTED: production registration and both method implementations exist, but the required Host dispatch test is missing.

## Host wire check

`dotnet/tests/Eve.Host.Tests/TaleManagerHostWireTests.cs:Tale_manager_calls_cross_the_host_wire_and_notify_the_current_system` sends both RPCs through the Host TCP dispatcher under strict RPC routing. Its read callback obtains an incursion record from a temporary PostgreSQL table; the test checks the cached global reply, the `OnTaleData` notification payload and ordering, and the client-compatible empty template result. It passed on `eve_p4` (1 passed, 0 failed, 0 skipped). The service-level regression at `dotnet/tests/Eve.Services.Core.Tests/IncursionAnomalyServiceTests.cs:65-117` and the neighboring incursion service tests also passed (7 passed, 0 failed, 0 skipped); the Host incursion/read-model regression passed (1 passed, 0 failed, 0 skipped).

Whole-client search found the only method callsites at `taleclient/tale_service.py:127-130,145-146`. The template lookup turns each result into `ActiveTaleThin` fields `tale_id`, `template_id`, `solar_system_id`, `start_date`, and `end_date`; `mapFilterVanguardNemesisConvoys.py:23-39` counts these by solar system. The global RPC result is memoized by the client; the UI state comes from `OnTaleData`: `tale_service.py:57-63` updates active tales, and `incursionSvc.py:68-72` starts the incursion UI from `templateClassID` and `taleID`. EveJS 261028.1 returns the empty cached list in `server/src/services/tale/taleMgrService.js:60-71`; eve.rs preserves that reply and emits the current-system notification from its persisted incursion read side. A graphical V24.01 client session was not run.
