# RPC Host coverage handoff — CX5

## Scope and decision

This batch adds one real-socket Host regression covering the listed lookup, machoNet, and moonExtractions reads. No production endpoint was added: the 15 rows named `machoNet.get` are Python dictionary lookups on the result of the existing RPC `machoNet.GetGlobalConfig()`, not remote method calls. Every listed callsite performs its `.get(key, fallback)` locally after the remote call.

## Client evidence and consumers

- RPC-01790/01791: `eve/client/script/ui/shared/neocom/corporation/corp_ui_applications.py:890-901` calls `LookupEvePlayerCharacters(searchStr, 0)`; handles None/empty, primes `each.characterID`, and builds invitation rows from that ID and owner cache.
- RPC-01794/01795: `eve/client/script/ui/util/searchOld.py:53-56` calls `LookupKnownLocationsByGroup(groupID, searchStr)`, extracts each `itemID`, and primes `cfg.evelocations`.
- RPC-01798/01799: `eve/client/script/ui/shared/neocom/wallet/noneNPCAccountOwnerDialog.py:64-78` calls `LookupNoneNPCAccountOwners(searchStr, 0)`; handles None/empty, primes `each.ownerID`, and builds selectable owner rows.
- RPC-01811: `eve/client/script/ui/shared/maps/mapSvc.py:428-440` unpacks `(sol, sta, statDivisor)`, reads per-system counts with dictionary `.get(..., 0)`, subtracts docked pilots, divides by the divisor, and builds map values.
- RPC-01830, 01831, 01832, 01833, 01835: local config dictionary consumers at `eve/client/script/ui/eveCommands.py:3570,3583,3596,3609,3622`. They read CSMVotingURL, CSMCandidateInfoURL, AcademyLandingPageURL, and UprisingPatchNotesURL; client fallback URLs are supplied at the callsites.
- RPC-01836 and 01852: `eve/client/script/ui/services/evePhotosvc.py:430-433,714-718` reads default portrait size (with a client constant fallback) and a caller-selected image-server key (with caller fallback).
- RPC-01837: `eve/client/script/parklife/autopilot.py:74-75` reads disableAutopilotLogging to decide whether to log.
- RPC-01838: `eve/client/script/ui/services/corporation/base_corporation.py:1048-1049` reads disableCorpRecruitmentLogging.
- RPC-01841: `carbon/client/script/remote/connectionService.py:296-297` reads disableLongCallWarning.
- RPC-01846: `eve/client/script/ui/shared/info/infosvc.py:2225-2227` reads enableDustLink before adding planet-control information.
- RPC-01847/01848: `eve/client/script/ui/services/flightControls.py:47-48,376-377` read flightControlsThrottle and flightControlStatisticsInterval, with client defaults.
- RPC-01849: `eve/client/script/ui/shared/info/characterInfoWindow.py:205-206` reads hideReportBot before adding the report action.
- RPC-01855: `eve/client/script/ui/shared/market/buySellMultiBase.py:251-253` reads the configured order cap, defaulting to 100, and limits selected items.
- RPC-02103/02104: `eve/client/script/ui/moonmining/schedulingSvc.py:36-40` returns an empty list without the station-manager role and otherwise returns the RPC list; `eve/client/script/ui/structure/structureBrowser/moonminingEventsCont.py:96-113` reads each extraction's structureID, solarSystemID, chunkAvailableTime, and moonID to build browser rows.
- RPC-02105: `eve/client/script/ui/moonmining/schedulingSvc.py:76-78` returns the resource-composition mapping; `eve/client/script/ui/moonmining/schedulingSvc.py:24-25` and `eve/client/script/ui/moonmining/scheduling.py:80-83` consume it for the moon-mining view.

Whole-tree Serena search for `GetGlobalConfig().get(` found these rows plus consumers in character creation, server info, globalConfig helpers, raffles, and monolithconfig. They also use the returned dictionary locally.

## EveJS and eve.rs behavior

EveJS v0.12.9:
- `server/src/services/corporation/lookupSvcService.js:447-458` filters character rows by search string and exact mode; lines 533-557 similarly build owner rows and delegate location-group lookups.
- `server/src/services/machoNet/machoNetService.js:340-343` returns the global config dictionary. Lines 500-509 return the client-consumed three-part map-stat shape with divisor 1.
- `server/src/services/structure/moonExtractionsService.js:1128-1165` filters active/fracturing extractions by corporation, orders by chunk-available time, and returns per-structure resource composition.

eve.rs already registers `lookupSvc` through `CoreServices.CreateLookupService`, `machoNet` through `MachoNetService`, and `moonExtractions` through `MoonExtractionsRpcService`. The regression uses those real services over the Host socket dispatcher. Lookup and moon reads use injected directory/state sources; machoNet uses its cluster config and live statistics provider. Reads make no mutations or notifications. The Host map-stat provider can return live values while EveJS returns safe empty maps; both meet the observed client shape and divisor use.

## Verification

- Focused real-Host wire regression: `Rpc01790HostCoverageTests.Lookup_machoNet_and_moon_read_calls_use_registered_host_routes_with_macho_version_kwargs`.
- Actions 40275193604 found nonexistent `Py` and `TestFront.Packets` helpers; corrected in a064ea7a.
- Actions 40275936800 found the missing Host `Packet` envelope and an `Eve.Services` import in the concurrent Slash Host test; corrected in bc20c71b.
- Actions 40276677603 built Host.Tests but the focused test timed out because the test sent RPC packets before Host's `Open` session message. The regression now opens the session and consumes initial state before RPC calls.
- The corrected change is pushed in `b1689998`. Selected run 40277519916 is in progress. The push-triggered `ci.yml` run 40277439667 was queued when last checked.
- A local focused `dotnet test` invocation is active but progressing slowly under shared-machine load; result pending.
- No graphical-client run was performed.
