# Deep check: sharedSettingsMgr

Source snapshot: main commit `7a3a02e9`; client: V24.01 eve-logic commit `1dbb5711a072a146e1d228365668ac05655eefe1`.

## Client call and reply flow

| Method | V24.01 callers and wire arguments | Reply consumed |
|---|---|---|
| `StoreSettingLinkAndGetID` | `eve/client/script/ui/shared/inventory/filterSvc.py:447-451`: positional setting type and item-filter payload; `eve/client/script/ui/control/eveEditPlainText.py:168-172`: positional broadcast setting type and payload | Both callers consume `util.KeyVal(hashvalue, sqID, settingTypeID)` to form the shared-setting URL. A missing key becomes `UserError('ItemFilterLoadingError')`. |
| `GetStoredSharedSetting` | `sharedSettings/util.py:8-19`: one positional setting-key tuple `(hashvalue, sqID, settingTypeID)` | Returns the stored YAML string; the helper raises the caller-supplied `UserError` if absent, then safe-loads YAML and converts the key/value list to a dictionary. Fleet broadcast loading reaches this helper at `eve/client/script/parklife/fleetSvc.py:2313`. |

The setting type is sent as the client integer constant; payload is the client’s serialized dict/list data. The service call has no keyword arguments. No notification follows either operation.

## Host and EveJS

Production construction is `dotnet/src/Eve.Services.Core/CoreServices.cs:79`; callbacks in `dotnet/src/Eve.Host/Composition/CharacterComposition.cs:225-237` read and durably flush the `sharedSettings` GameStore table. `SharedSettingsMgrService` in `dotnet/src/Eve.Services.Core/GameRpcServices.cs:1056-1140` returns a KeyVal for a valid stored payload, deduplicates matching payloads, and returns None for invalid keys or failed persistence. Reads return the stored payload as a Python string.

EveJS v0.12.9 uses the same two methods in `server/src/services/settings/sharedSettingsMgrService.js:263-276` and :278-290. The five selected CSV rows RPC-02257–02261 are marked IMPLEMENTED-TESTED.

## Evidence and limits

Registered Host wire coverage is in `dotnet/tests/Eve.Host.Tests/SharedSettingsHostWireTests.cs:13-27`; the broader registered-service persistence, duplicate, concurrency and invalid-input coverage is in `FilterServiceDeepCheckHostWireTests.cs:41-115`. The Host test files were not executed for this documentation-only batch. No PostgreSQL database or graphical client was used.
