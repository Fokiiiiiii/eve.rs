# Deep check: industryMonitor

Source snapshot: main commit `7a3a02e9`; client: V24.01 eve-logic commit `1dbb5711a072a146e1d228365668ac05655eefe1`.

## Client call and reply flow

The four selected CSV rows RPC-01585–01588 describe two methods. In the whole client tree, `ConnectJob` has no caller outside its own definition in `eve/client/script/industry/industrySvc.py:304-307`. If called, it sends one positional `job.dump()` object; the client assigns the returned tuple `(monitorID, available)` to the job. `DisconnectJob` sends one positional monitor ID or None at :310-313 and is called when the industry window closes (`eve/client/script/ui/shared/industry/industryWnd.py:106-108`).

The notification receiver is `industrySvc.py:323-329`: `OnIndustryMaterials(jobID, materials)` checks the returned monitor ID and replaces the job’s available-materials map. `industry/job.py:480-488` applies that map to each material; `industry/material.py:77-90` reads quantity by type ID. This confirms the notification’s second argument is a typeID-to-quantity mapping. No client-visible UserError path exists.

## Host and EveJS

Production registration is through `dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:42-46,79-80`. Connect returns a Python tuple of monitor ID and material dict; Disconnect returns None. Monitor rows and the next ID are stored under `industryRuntime`, while active watches are session-local and are discarded at disconnect/restart. Inventory mutations pass through `IndustryInventoryChangePublisher.cs:5-31`; changed materials trigger `OnIndustryMaterials` after the inventory change.

EveJS v0.12.9 `server/src/services/industry/industryMonitorService.js:20-33` accepts one request object, returns the same two-element tuple and treats disconnect as a no-result operation. Host wire/notification checks exist in `dotnet/tests/Eve.Host.Tests/IndustryHostRpcTests.cs:82-116` and `IndustryMonitorNoticeTests.cs:109-190`; the latter verifies `OnItemChange` precedes `OnIndustryMaterials` and isolates sessions.

The CSV’s CLIENT-DEAD classification is accurate for ConnectJob in the current V24 client: no call reaches the server today, and the only located disconnect comes from window close. The Host implementation and tests remain useful for a reactivated client path, but no live-client flow was verified in this batch. Tests were not executed; no PostgreSQL database or graphical client was used.
