# Deep check: inventory filter service

Client source checked: V24.01 build 3396210 in `/home/user/eve-logic`. This is a source and Host-wire review; the graphical client was not launched.

## Finding

There is no remote `filterSvc` service in the client. `eve/client/script/ui/shared/inventory/filterSvc.py:62` defines `ItemFilterSvc` as the local service `svc.itemFilter`. Whole-tree searches for `GetService('itemFilter')`, `RemoteSvc('filterSvc')`, `ConnectToRemoteService('filterSvc')`, and their `itemFilter` remote variants found only the local calls listed below. There is no bound-object or moniker call for this service. eve.rs has no Host route named `filterSvc` or `itemFilter`.

The local service makes remote calls only through `charMgr` (saved per-character settings) and `sharedSettingsMgr` (shared links). The relevant whole-client callsites and consumers are included here, including the second fleet caller of the shared-settings methods.

## Every externally called method of local `itemFilter`

| Method | Whole-tree client callsites | Client-visible behavior and errors |
| --- | --- | --- |
| `FilterItems` | `eve/client/script/ui/shared/container.py:615`; `eve/client/script/ui/shared/fittingScreen/invPanel.py:332-334` | Positional `(items, filter)`; returns a lazy filtered iterator. Its local implementation calls `FilterItem`, `GetValue`, and `_Filter` at `filterSvc.py:93-193`. |
| `ApplyTempFilter` | `container.py:618`; `fittingScreen/invPanel.py:337` | Positional `items`; returns the same items when no temporary filter exists, otherwise a filtered iterator. |
| `SetTempFilter` | `filterSvc.py:402,406` | Positional filter tuple or `None`; updates local UI state and scatters local `OnInvTempFilterChanged` at `filterSvc.py:305-308`. |
| `SaveFilter` | `filterSvc.py:426,428` | Positional `(name, resultType, conditions)`; the edit path also passes local Python keyword `suppressWarn=True`. Empty names raise local `UserError('SaveFilterNoName')` at `filterSvc.py:256-258`; names over 100 characters are truncated. A successful save stores the filters through `characterSettings.Save` and scatters local `OnInvFiltersChanged` at `filterSvc.py:244-274`. |
| `RemoveFilter` | `filterSvc.py:425`; `invFilters.py:238` | Positional name; the edit path passes local `suppressWarn=True`. An existing filter is removed and persisted; a missing filter causes no mutation. |
| `CreateFilter` | `invFilters.py:70` | No arguments; opens the local filter editor. |
| `GetFilters` | `invFilters.py:129` | No arguments; returns the local filter list sorted by lower-case name. |
| `EditFilter` | `invFilters.py:243` | Positional name; opens the local editor only when the filter exists. |
| `LoadSharedFilter` | `sharedSettings/client/link.py:17` | Positional setting-key tuple; prompts for confirmation, loads the shared mapping and opens the editor. A missing remote payload becomes client `UserError('ItemFilterLoadingError')` through `sharedSettings/util.py:9-20,23-27`. |

The external calls above are the complete whole-tree `sm.GetService('itemFilter')` call set. The service's `GetFilterByName`, `FetchFiltersFromServer`, `PersistFilters`, `GetDefaultFilters`, and filtering helpers are implementation methods, not separately invoked through a client service reference. `suppressWarn` is never an RPC keyword.

On startup, `Run` loads default filters and the local `characterSettings` service. The lazy `filtersByName` property reads the saved `inventoryFilters` YAML; absent data selects the defaults, while saved data is decoded into the name map (`filterSvc.py:67-87,234-243`). `GetFilters` returns that state to the inventory UI. Save/remove update the local map and persist it through `charMgr`; the next service read reloads the saved mapping. Temporary filters remain local, and a shared link opens the editor without saving until the user submits it.

## Remote methods reached by the local service

These are ordinary positional remote calls. The client RPC transport adds `machoVersion` in kwargs (`carbon/common/script/net/ServiceCallGPCS.py:667-704`). eve.rs strips that transport-only kwarg before invoking the handler (`dotnet/src/Eve.Services/PacketDispatcher.cs:217-220,307-317`). The Host packet helper uses the same kwargs (`dotnet/tests/Eve.Host.Tests/Rpc01800To02100HostWireTests.cs:12-18`). No bound or moniker call is involved.

| RPC method | All callers and argument forms in the client | Host route, consumed reply, state, errors and notifications |
| --- | --- | --- |
| `sharedSettingsMgr.StoreSettingLinkAndGetID` | Item-filter link: `eve/client/script/ui/shared/inventory/filterSvc.py:372-383,448-450`, positional `(2, {'filter': (...)})`. Fleet-broadcast sharing: `eve/client/script/ui/control/eveEditPlainText.py:169`, positional `(3, entry.data)`. | Registered as `sharedSettingsMgr` at `dotnet/src/Eve.Services.Core/GameRpcServices.cs:1056,1066-1117`, added to Core Host services by `dotnet/src/Eve.Services.Core/CoreServices.cs:79`. It returns `util.KeyVal` fields `hashvalue` (string/bytes), `sqID` (integer), and `settingTypeID` (integer). It persists and deduplicates payloads via the shared-settings callback; production stores the `sharedSettings` GameStore root (`dotnet/src/Eve.Host/Composition/CharacterComposition.cs:226-235`). Unsupported type, `None`, empty payload, or rejected persistence returns `None`; the client turns a missing key into `UserError('ItemFilterLoadingError')` (`filterSvc.py:449-450`; generic helper `sharedSettings/util.py:23-27`). No Host notification follows. |
| `sharedSettingsMgr.GetStoredSharedSetting` | `sharedSettings/util.py:9-20` is used by item filters and fleet settings. The single positional key is the tuple `(hashvalue, sqID, settingTypeID)` produced by `sharedSettings/client/link.py:31-39`. | Registered on the same service at `GameRpcServices.cs:1119-1148`. It accepts numeric key fields encoded as either `PyInt` or `PyLong`; success is the stored payload as a Python 2 `str` (Host wire `PyBytes`), and malformed/missing keys return `None` (`GameRpcServices.cs:1230-1287`). The client runs `yaml.safe_load` and rebuilds a mapping (`sharedSettings/util.py:16-20`); `None` raises `UserError('ItemFilterLoadingError')` (`sharedSettings/util.py:23-27`). No Host notification follows. |
| `charMgr.GetCharacterSettings` | `eve/client/script/ui/services/characterSettingsSvc.py:21-42`; no method arguments. | Production service list constructs `CharMgrService` with the persistence read/write model (`dotnet/src/Eve.Host/Rpc/CharacterRpc.cs:73-85`; `CharacterServiceList.cs:110-111`). The handler returns a `PyDict` keyed by string (`dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:357-364`); the client only indexes the mapping and catches missing-key `KeyError`, so absent setting reads as `None` (`characterSettingsSvc.py:31-42`). The read model reads `characters/{characterID}/characterSettings` (`PersistenceCharMgrReadModel.cs:104-124,461-464`). No notification follows. |
| `charMgr.SaveCharacterSetting` | `characterSettingsSvc.py:44-51`; filter persistence passes positional `('inventoryFilters', yaml.safe_dump(filters))` from `filterSvc.py:244-247`. | Returns `None`, updates character settings and writes the GameStore path (`CharMgrService.cs:447-472`; `PersistenceCharMgrReadModel.cs:309-320`). Invalid key/value bounds raise wrapped `eveexceptions.UserError('CustomInfo')` with an `info` detail (`CharMgrService.cs:448-463`); the Host-wire regression below asserts this error. The ordinary client wrapper rejects text over 102400 characters locally with `RuntimeError` before sending (`characterSettingsSvc.py:44-48`). No notification follows. |
| `charMgr.DeleteCharacterSetting` | `characterSettingsSvc.py:53-58`; the wrapper invokes it only when the setting exists. | Positional setting key; returns `None`, removes the key, and emits no Host notification (`CharMgrService.cs:474-488`; `PersistenceCharMgrReadModel.cs:309-320`). The filter path ordinarily saves a non-empty YAML string. The generic wrapper's `Save(key, None)` first calls Delete and then still calls Save with `None` (`characterSettingsSvc.py:44-51`). The existing Host round-trip test checks the resulting stored `None` value. |

For the local UI service, `SaveFilter`'s `SaveFilterNoName` and the shared-link loading error are client-side errors; they are not Host RPC error replies. Host shared-settings failures are `None` replies and are converted by the client helper into `ItemFilterLoadingError`. There are no server-side item-filter notifications. `OnInvFiltersChanged` and `OnInvTempFilterChanged` are local client scatters only (`filterSvc.py:244-247,305-308`).

The `charMgr` wrapper is shared outside inventory filters. Whole-tree callers also read or write other settings through it: `eve/client/script/ui/services/mail/mailSvc.py:1351,1360,1391`, `eve/client/script/parklife/fleetSvc.py:131,2279`, and `xmppchatclient/xmppchatsvc.py:1716,1733`. The inventory-filter path uses only the `inventoryFilters` key; the same registered handlers and persisted per-character mapping serve these sibling consumers.

## EveJS 261018.1 comparison

`src/server/src/services/settings/sharedSettingsMgrService.js:263-293` implements the same two shared-settings methods. It returns a KeyVal or `None` from Store and a payload string or `None` from Get; its payload is YAML (`48-66`). eve.rs stores the payload as deterministic JSON, which the client consumes through `yaml.safe_load` (`sharedSettings/util.py:16-20`). EveJS returns a key even when its repository write result is not checked (`188-191,263-276`); eve.rs returns `None` if the production persistence callback refuses the write. This matches the client's loading-error path instead of returning a link that cannot be loaded.

EveJS character-setting handlers also return `None` and use per-character settings (`src/server/src/services/character/charMgrService.js:1223-1234`; `characterSettingsState.js:111-143`). Its Get response is a named KeyVal; eve.rs returns a mapping, which is what the client indexes. These outer-shape differences do not change the client-visible use.

## Host-wire coverage and limits

`dotnet/tests/Eve.Host.Tests/FilterServiceDeepCheckHostWireTests.cs` exercises the registered Host packet dispatcher for filter-link store/read, both shared-setting type IDs, duplicate and concurrent writes, PyInt/PyLong key fields, None and malformed inputs, declined persistence, and charMgr settings read/save/delete/None. The additional `FilterServiceErrorHostWireTests.Character_filter_setting_rejects_non_scalar_key_with_client_user_error_over_host_wire` asserts the exact Host `UserError('CustomInfo')` envelope and detail. These packet-loop tests use no PostgreSQL. Production composition/persistence wiring was checked in source; a database-backed flush/restart and graphical-client run were not performed.

## Local verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` passed after the main merge (0 warnings, 0 errors).
- Focused Host wire tests passed 3/3 before the main merge and 5/5 after it; both runs had 0 failures and 0 skips.
- The full `Eve.Host.Tests` shard after the main merge, with `EVE_PG_URL` unset, finished 602 passed, 101 failed, and 228 skipped (931 total). The target filterSvc tests all passed. The full shard is not green; database-backed fixtures report that `EVE_PG_URL` is absent (for example `MoonOwnerNotificationTests`). No PostgreSQL connection was attempted or made. The graphical client was not launched.
