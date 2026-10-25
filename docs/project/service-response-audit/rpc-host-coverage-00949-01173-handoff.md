# RPC Host coverage handoff: 00949–01173

## Status

In progress. Added one real Host socket regression for the registered `cache.GetServerVersionAndBuild` route and the concrete `clientStatLogger.LogString` service. The logger change now accepts the marshal string forms exposed by `JsSemantics.StringValue`, preserving the 4096-byte limit and malformed-input errors.

The rows named `billMgr.get` and `bountyProxy.clear_memoized` do not describe remote service calls in V24.01. They are local Python dictionary/cache operations; adding server methods for them would create endpoints the client does not invoke.

## V24.01 client evidence and EveJS comparison

| Rows | Client caller and consumer evidence | EveJS v0.12.9 reference |
|---|---|---|
| 00949 | `eve/client/script/ui/shared/neocom/wallet/panels/corp/corpBillsAutoPaySettingsPanel.py:83` sends the settings snapshot; the UI builds/reads it at lines 36, 59–78. No result is consumed. Other `billMgr` callers are the corporation bill panels and wallet service. | `server/src/services/account/billMgrService.js:232-261` validates the submitted owner set and persists settings. |
| 00950 | Line 59 is `self.automaticPaymentSettings.get(...)`, a local dict lookup. The actual remote settings read is `corpBillsAutoPaySettingsPanel.py:36` (`GetAutomaticPaySettings`). | No `billMgr.get` handler; `GetAutomaticPaySettings` is the service operation. |
| 00951 | `eve/client/script/industry/blueprintSvc.py:95`; the returned instance is passed to `industryCommon.BlueprintInstance`. | `server/src/services/industry/blueprintManagerService.js:31-35`. |
| 00952–00953 | `blueprintSvc.py:149` consumes both values as blueprints and owner counts; line 136 calls the facility-filtered form and consumes the first value. | `blueprintManagerService.js:37-53` returns a pair of blueprint rows and counts. |
| 00954 | `blueprintSvc.py:24` reads `['maxBlueprintResults']` from the limits mapping. | `blueprintManagerService.js:24-29`. |
| 00955 | `eve/client/script/ui/shared/bountySvc.py:81-85`; updates local bounty cache and clears the local cached `GetMyBounties` result. Bounty UI callers include `bountyWindow.py:446,1006`. | `server/src/services/bounty/bountyProxyService.js:378-460`. |
| 00956 | `bountySvc.py:254-258`; removes the local kill-right tracker entry and scatters `OnKillRightSellCancel`; UI caller: `killRights.py:119`. | `bountyProxyService.js:556-592`. |
| 00957–00958 | `bountySvc.py:234-241`; developer slash commands call these at `eve/devtools/script/svc_slash.py:1531,1535`. | `bountyProxyService.js:636-640` and `:630-634`. |
| 00959 | `bountySvc.py:146-148`; rows feed cache refresh at lines 132–139 and UI owner totals at `bountyWindow.py:389,578,982`. | `bountyProxyService.js:462-469`. |
| 00960 | `bountySvc.py:287-289`; available rights are consumed by the kill-right tracker. | `bountyProxyService.js:512-525`. |
| 00961 | `bountySvc.py:227-228`; consumed by `bountyWindow.py:554`. | `bountyProxyService.js:492-500`. |
| 00962 | `bountySvc.py:270-276`; cached result feeds kill-right checks and `killRightsPanel.py:30`. | `bountyProxyService.js:502-510`. |
| 00963–00965 | `bountySvc.py:217-224`; bounty-window leaderboards consume the first result at `bountyWindow.py:541-545`. | `bountyProxyService.js:614-616,610-612,606-608`. |
| 00966 | `bountySvc.py:261-267`; returned rows update the local kill-right tracker; UI submits at `killRights.py:324`. | `bountyProxyService.js:527-554`. |
| 00967–00968 | `bountySvc.py:50,84` call the decorator-provided local `clear_memoized` on `GetMyBounties`. These are not calls to `sm.ProxySvc('bountyProxy')`. Remote invalidation is separately implemented at lines 347–349. | No remote `clear_memoized` method in `bountyProxyService.js`; `GetMyBounties` is the remote operation. |
| 00977–00978 | `eve/client/script/ui/services/bugReporting.py:191-192` unpacks server version and build into the bug report at lines 193–205. | `server/src/services/cache/cacheService.js:24-26`. |
| 01171–01172 | `eve/client/script/util/jumpMonitor.py:96,127,132` reports jump diagnostics; `eve/client/script/remote/michelle.py:354` reports a ballpark timeout. Return is ignored. | `server/src/services/logging/clientStatLoggerService.js:35-40`. |
| 01173 | `eve/client/script/ui/services/sovereigntyResourceSvc.py:263-268` forwards the remote rows into its five-second memoized getter; consumers include `sovHubEntryController.py:138` and `skyhookBrowserController.py:25`. | `server/src/services/sovereignty/colonyResourcesMgrService.js:108-121`. |

Whole-tree searches found the direct `billMgr`, `bountyProxy`, `cache`, `clientStatLogger`, and `colonyResourcesMgr` callers listed above. Local row consumers are cited in the table; the response fields still need end-to-end checks for the remaining RPCs.

## Changes and checks

- `dotnet/src/Eve.Host/CharacterRpcServices.cs`: `LogString` now accepts byte and wide-string marshal values through `JsSemantics.StringValue`; invalid types still error and the UTF-8 byte limit remains 4096.
- `dotnet/tests/Eve.Host.Tests/HostClientRpcCoverageTests.cs`: one socket test uses the real Host registry and services, checks the version/build tuple, two log calls, and the logger side effect.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` — passed, 0 warnings/errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-build -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Cache_version_and_client_logs_round_trip_through_the_host_registry'` — passed, 1 test, 0 skipped.
- No database was used. The graphical client was not exercised.

## Push and Actions status

- Pushed commit: `6b4380a6faef` (includes the logger fix, socket regression, and this handoff).
- Agent-test run `40193473051`: passed for `Eve.Host.Tests`, focused filter `Cache_version_and_client_logs_round_trip_through_the_host_registry`; no failed tests or jobs.
- Push-triggered CI run `40193463753`: queued when last checked. Its result is pending.

## Remaining work

Host socket regressions and full runtime registration/state tracing remain for bill settings, blueprints, bounty/kill-right operations and corporation skyhooks. Validate the exact production registry composition for each service and compare each tested success/error/state path with EveJS before closing this batch. The local-only rows 00950, 00967 and 00968 should be corrected in the coverage source instead of implemented as RPCs.
