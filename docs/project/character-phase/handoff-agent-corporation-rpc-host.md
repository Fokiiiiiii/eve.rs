# Corporation client RPC Host coverage handoff

## Current status

The PostgreSQL-free real Host wire regression in `dotnet/tests/Eve.Host.Tests/CorporationClientRpcWireTests.cs` registers production `CorpMgrService` and `CorporationService`, sends calls with transport-added `machoVersion`, and checks a 500-entry non-empty `GetAggressionSettingsForCorps` result, `GetCorpInfo(0)` returning `None`, and an empty medal-recipient rowset. The handler now returns a non-null injected aggression read before consulting its persisted fallback, so the authoritative reader is usable without opening a corporation-store connection. This verifies the returned `PyDict` keys/settings through Host dispatch; it does not finish RPC-01290–RPC-01316.

No PostgreSQL database was used. Positive-state tests for the remaining corporation and medal calls depend on persisted corporation/character data and remain outstanding.

## V24.01 callers and consumers

| RPC rows | Client callsites and consumed shape |
|---|---|
| RPC-01290–01295, `corpmgr.GetPublicInfo` | Direct callers include `bountyWindow.py:410`, `corporationInfoWindow.py:48,183`, `killRights.py:151`, and `warPermit.py:42`. The bounty UI reads public corporation fields at `bountyWindow.py:411`; corporation info reads `corporationID`, `ceoID`, `description`, `deleted`, and `stationID` at `corporationInfoWindow.py:60,66,184-215`; kill rights reads `allianceID` at `killRights.py:152-153`. |
| RPC-01296–01297, `corpmgr.SearchAssets` | `corp_ui_accounts.py:751` supplies bucket, category, group, type and quantity; it iterates returned rows and reads `locationID` at `corp_ui_accounts.py:752-773` to derive solar system, labels and location groups. |
| RPC-01298, `corpmgr.iteritems` | This is the Python dictionary consumer of `GetAggressionSettingsForCorps`, called in `base_corporation.py:272`; a second whole-tree caller passes recruitment corporation IDs at `:1111`. The loop at `:273-275` caches each corporation/settings pair and returns the merged mapping, while `:1111-1115` looks up each advert's corporation and uses its settings for the friendly-fire label. The server operation is `GetAggressionSettingsForCorps`; `iteritems` is not a separate RPC. |
| RPC-01299–01302, `corporationSvc.CreateMedal` | The wrapper in `medals.py:31-47` sends title, description and graphics; after a confirmation error it retries with the fourth positional `True` at `:46`. `medalribbonranks.py:561` is another wrapper caller. |
| RPC-01303–01304, `GetAllCorpMedals` | `medals.py:59-60` unpacks the medal and graphics rowsets. The member decorations window consumes them at `corp_ui_member_decorations.py:53`; menu code consumes the result at `menuFunctions.py:59`. |
| RPC-01305, `GetCorpInfo` | `infosvc.py:1165` calls this for an NPC corporation; the caller checks the returned faction information at `infosvc.py:1170` and uses NPC corporation size/extent at `:1251,1260`. |
| RPC-01306–01307, `GetEmployementRecordAndCharacterTransfers` | `infosvc.py:2841` unpacks history and transfers. The history consumer reads `corporationID`, `deleted`, and `startDate` at `:2844-2867`; transfer rows feed the subcontent at `:2870-2885`. |
| RPC-01308–01309, `GetEmploymentRecord` | `facWarSvc.py:293` calls this for the session character. The nearby service flow invalidates the cached method after enlistment changes at `facWarSvc.py:207`; `base_corporation_ui.py:99` is another cache invalidation caller. |
| RPC-01312–01313, `GetMedalDetails` | `medals.py:90-91` forwards the call. Info panels consume `info[0]` at `infoContentBase.py:675,1450`; details include medal fields and graphics. |
| RPC-01314–01315, `GetMedalsReceived` | `medals.py:87-88` forwards the two-rowset tuple. Character decoration views consume it at `decorationsPanel.py:113,203` and `charSheetUtil.py:35`; the flag filter reads each medal's `status` and `medalID` at `medals.py:70-84`. |
| RPC-01316, `GetRecipientsOfMedal` | `medals.py:63-65` returns recipient rows; `corp_ui_member_decorations.py:110` consumes them and `medals.py:65-67` accesses `reason`. |

The whole-tree client search also found additional `charMgr.GetPublicInfo` calls, which are a different service and outside these rows.

## EveJS comparison and current eve.rs wiring

EveJS v0.12.9 implements `GetPublicInfo` in `server/src/services/corporation/corpmgrService.js:459-493`, `SearchAssets` at `:596-625`, and `GetAggressionSettingsForCorps` as a dictionary at `:520-528`. Corporation calls are in `server/src/services/corporation/corpService.js`: employment `:440-497`, `GetCorpInfo` `:480-492`, medals received/details `:499-511`, create `:523-545`, and lists/recipients `:567-605`. EveJS `GetCorpInfo` returns `null` for a missing corporation and an empty iterable activity list for an existing corporation.

Production registration is in `dotnet/src/Eve.Host/Rpc/CorporationRpc.cs:46,60`. The current `GetAggressionSettingsForCorps` handler in `CorpMgrRpcServices.cs:66-93` accepts list, tuple or dictionary keys, enforces a 500-item bound, and returns a `PyDict`; the regression currently exercises only the empty-list path. The current `GetCorpInfo` handler in `CorporationService.cs:68-75` returns `None` for id 0 and an empty list for a stored corporation.

## Verification and remaining work

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/src/Eve.Services.Corporations/Eve.Services.Corporations.csproj --no-restore -p:UseSharedCompilation=false -p:BuildProjectReferences=false` — passed, 0 warnings and 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:UseSharedCompilation=false -p:BuildProjectReferences=false` — passed, 0 warnings and 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-build --filter 'FullyQualifiedName~Corporation_read_rpcs_keep_client_shapes_through_host_with_transport_kwargs' -p:UseSharedCompilation=false` — passed, 1 test, 0 skipped.
- Pushed SHA `93a5c7b5` passed agent-test run `40173516530`; its push-triggered `ci.yml` run `40173509928` remained in progress at the latest check. Pushed SHA `34a9e462` passed agent-test run `40174450658`; its push-triggered `ci.yml` run `40174443344` was queued at the latest check.
- Graphical-client behavior was not exercised.

The remaining requested Host checks are positive-state calls for public corporation info, asset search, medal creation/list/details/received, and employment history. They were not included because those paths read persisted corporation/character state and this task forbids using a PostgreSQL database. Do not close the RPC rows based on the test above.
