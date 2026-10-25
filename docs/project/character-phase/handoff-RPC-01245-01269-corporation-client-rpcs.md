# Corporation client RPC handoff: task rows RPC-01245–RPC-01269

## Status

The 25 task rows group into seven methods: recruitment ad list/search/update; audit member; single/bulk aggression settings; and corporation asset location inventory. The PostgreSQL-free Host regression is `dotnet/tests/Eve.Host.Tests/CorporationClientRpcCoverageWireTests.cs`. It registers production `CorpMgrService` and `CorpRecruitmentProxyService` in the real Host registry and sends `CallWithKwargs` with transport-added `machoVersion`. It checks single and 500-ID bulk aggression replies, the empty recruitment list with no selected character, and the two-CRowset audit tuple with empty `lines`.

The repository client matrix numbers these same eight callers as RPC-01241–RPC-01265, four below the task row IDs; each row still matches the task's stated method and client file:line. The matrix evidence is `docs/project/service-audit/CLIENT_CALL_MATRIX.csv:1241-1265`.

No PostgreSQL database was opened. Recruitment search, successful recruitment update, and authorized asset rows require persisted corporation/member/item/office state and were not exercised in this batch. The graphical client was not exercised.

## V24.01 call and reply consumers

| Task rows | Caller and consumer |
|---|---|
| RPC-01245 `corpRecProxy.GetRecruitmentAdsForCorporation` | `bco_recruitment.py:44-51` calls the proxy and indexes each advert by `(corporationID, adID)`; `corp_ui_recruitment.py:46-51` iterates the list and primes owner records. |
| RPC-01246 `corpRecProxy.SearchCorpAds` | `base_corporation.py:656-657` forwards all 11 filter values. Its UI caller is `corpRecruitmentContainer.py:1240-1242`; that path passes the returned adverts to `MakeRecruitmentEntriesFromAdList`. |
| RPC-01247 `corpRecProxy.UpdateRecruitmentAd` | `base_corporation.py:641-642` forwards the eight positional values and the `minSP` / `otherMask` keywords; `corpRecruitmentContainer.py:652-657` calls it when saving an existing advert. |
| RPC-01248–01249 `corpmgr.AuditMember` | `corp_ui_member_auditing.py:175-182` unpacks the event and role-history rowsets, iterates the event rows, and displays role history from the second result. |
| RPC-01250–01251 `corpmgr.GetAggressionSettings` | `base_corporation.py:255-260` caches the object. Friendly-fire consumers include `corp_ui_recruitment.py:177-178`, `corp_ui_applications.py:476-477`, and `infosvc.py:1223-1228`; the settings object's `_enableAfter` and `_disableAfter` drive that status. |
| RPC-01252–01253 `corpmgr.GetAggressionSettingsForCorps` | `base_corporation.py:262-275` sends uncached IDs and consumes the corporation-keyed mapping into the cache. Recruitment ads also fetch in bulk at `base_corporation.py:1110-1114` and use each advert corporation's friendly-fire setting. |
| RPC-01254–01255 `corpmgr.GetAssetInventory` | Contract creation requests `deliveries` at `createContractWnd.py:1560-1564`; it reads each `locationID`. |
| RPC-01256–01257 | `GetAssetInventory` with `offices` at `createContractWnd.py:1553-1558`; the UI uses `locationID` to build office choices. |
| RPC-01258–01259 | Map view caller `mapViewColorHandler.py:704-708`; it reads each `locationID` and maps it to a solar system. |
| RPC-01260–01261 | Star map caller `starModeHandler.py:659-663`; it reads each `locationID` and maps it to a solar system. |
| RPC-01262–01263 | `mapFilterCorpAssets.py:15-19`; it reads `locationID` to build the filter's system data. |
| RPC-01264–01265 | `mapFilterCorpAssets.py:88-92`; the office filter likewise groups by `locationID`. |
| RPC-01266–01267 | `corp_ui_accounts.py:99-102`; the asset-change handler matches a row's `locationID` to the station being refreshed. |
| RPC-01268–01269 | `corp_ui_accounts.py:229-233`; asset filtering derives its options from returned location rows. |

The matrix also records adjacent `GetAssetInventoryForLocation` callers in `corp_ui_accounts.py:422,470`; those are outside this batch.

## EveJS v0.12.9 and eve.rs

EveJS returns advert lists for `GetRecruitmentAdsForCorporation` (`server/src/services/corporation/corpRecProxyService.js:206-215`) and `SearchCorpAds` (`:244-270`). Its update mutates the advert, notifies recruitment-ad change, and returns null (`:300-320`). eve.rs implements these operations against the corporation store in `dotnet/src/Eve.Services.Corporations/CorpRegistryService.cs`; update persists the change and announces it only when the store reports a change.

EveJS `AuditMember` returns two rowsets and an empty payload when member/access is unavailable (`server/src/services/corporation/corpmgrService.js:530-552)). eve.rs returns two CRowsets with event and role-history columns (`dotnet/src/Eve.Services.Corporations/CorpMgrRpcServices.cs:83-158`). EveJS single aggression settings return the settings object, and bulk reads return a corporation-keyed mapping (`:507-528)); eve.rs emits `_enableAfter` / `_disableAfter` and the same mapping (`CorpMgrRpcServices.cs:63-81,213-227`). EveJS asset inventory returns a cached location CRowset (`:554-572)); eve.rs builds the cached CRowset from authoritative items/offices (`CorpMgrRpcServices.cs:161-169,408-444`).

Host registration includes both production services in `dotnet/src/Eve.Host/Rpc/CorporationRpc.cs`. This test verifies the registered Host routes, not a direct handler call.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` — passed, 0 warnings, 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-build --filter 'FullyQualifiedName~Corporation_client_reads_route_through_host_with_transport_kwargs' -p:UseSharedCompilation=false` — passed, 1 test, 0 skipped.
- An earlier `--no-restore` build stopped before compilation because `obj/project.assets.json` was absent; the restore-enabled build above passed.
- Search/update success and authorized asset inventory states, database restart behavior, and the graphical client were not exercised.
