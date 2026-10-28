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

## Test gap

`dotnet/tests/Eve.Services.Core.Tests/IncursionAnomalyServiceTests.cs:65-111` checks the notification payload and cache response at the service boundary. A whole-client test-source search found no registered Host wire test for either method. The service-level test was not executed in this documentation-only batch. Update the CSV classification and add a registered Host wire regression before declaring the service’s wire coverage complete. No PostgreSQL database or graphical client was used.
