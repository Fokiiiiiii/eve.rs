# Deep check: overviewPresetMgr

Source snapshot: main commit `7a3a02e9`; client: V24.01 eve-logic commit `1dbb5711a072a146e1d228365668ac05655eefe1`.

## Client call and reply flow

| Method | V24.01 caller and arguments | Reply consumed |
|---|---|---|
| `StoreLinkAndGetID` | `eve/client/script/ui/control/eveEditPlainText.py:155-164`, one positional overview-preset payload (`entry.data`) | On None the editor raises exact `UserError('OverviewProfileLoadingError')`; otherwise it reads `hashvalue` and `sqID` to build the link. |
| `GetStoredPreset` | `eve/client/script/parklife/overview/presetservice.py:866-876`, one positional preset key tuple | On None the loader raises exact `UserError('OverviewProfileLoadingError')`; otherwise it parses the YAML string, converts the pairs to a dict, and loads the profile. |

Neither method uses keywords or a macho-version argument. These are shared-data reads/writes without follow-up notifications.

## Host and EveJS

Production factory registration is `dotnet/src/Eve.Services.Core/CoreServices.cs:80`; the Host callback uses the persisted `overviewSharedPresets` GameStore table (`dotnet/src/Eve.Host/Composition/CharacterComposition.cs:225-229`). `OverviewPresetMgrService` in `dotnet/src/Eve.Services.Core/StructureDataRpcServices.cs:223-301` returns the two-field KeyVal and retrieves the stored YAML; invalid/missing data or a persistence failure returns None so the client raises its own UserError.

EveJS v0.12.9 implements matching handlers in `server/src/services/overview/overviewPresetMgrService.js:512-524` and :526-540. The four selected CSV rows RPC-02027–02030 are marked IMPLEMENTED-TESTED.

## Evidence and limits

Real Host dispatch coverage is in `dotnet/tests/Eve.Host.Tests/Rpc02058To02870HostWireTests.cs:58-93`; persistence across service construction is covered by `StructureDataRpcPersistenceTests.cs:52-71`. These tests were not executed for this documentation-only batch. No PostgreSQL database or graphical client was used.
