# Config and Beyonce audit handoff

Updated: 2026-10-29

## Verified client contract

- `eve/common/script/sys/eveCfg.py:604-620` declares the seventh location column `locationNameID` as `DBTYPE_I4`. `eveui/dragdata.py:75-78` and `eveui/autocomplete/location/suggestion.py:56-60` use a truthy message ID for localization and otherwise display `locationName`.
- Client requests for warp subjects are forwarded by `eve/client/script/remote/michelle.py:732-740`. `movementFunctions.py:461-463` sends `operationDungeon`; `:487-490` sends `moonMiningPoint`. `objectives/client/objective_task/dungeon.py:339-370`, `listSurroundingsBtn.py:380-382`, `escalationContentPiece.py:73-75`, `transmissionMgr.py:141-143`, and `jobboard/client/features/dungeons/escalation_sites/job.py:56-58` send `epinstance` or `externalDungeon`. `mecDen/mercDenWnd.py:320-323` supplies `mercenaryDen` to the stateful location button; `statefulButtonLocationController.py:148-153` sends it via `WarpToItem`.
- The docking UI invokes `CmdTurboDock` only on the GM key chord with client `session.role & ROLE_GML` at `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:509-516`.
- `carbon/common/script/net/moniker.py:142-144` unpacks the `MachoBindObject` response as `boundObject, retval`. EveJS 261028.1's `Handle_MachoBindObject` calls `buildBoundObjectResponse`, which returns a two-element response (`server/src/services/ship/beyonceService.js:4388-4423`, `server/src/services/_shared/serviceHelpers.js:825-943`).

## Changes

- Location rows for the (none) key and named structures use `None` for `locationNameID`; the persisted or fallback-readable label stays in `locationName`. The numeric type follows the client declaration even though EveJS 261028.1 currently builds string labels into this column (`server/src/services/config/configService.js:527-812`).
- A bind refused for a different location or absent in-space session returns the standard two-item response with a session-bound object and `None` nested result, without running the nested call. That lets the V24.01 moniker assign `persistantObjectID` after unpacking; a Host wire regression checks the response.
- The five additional warp subjects are intentionally refused with the existing client-facing `CustomNotify` warp failure. There is no eve.rs authority that maps their supplied identifier to a live, session-authorized destination. The request does not carry a destination position, and resolving an unrelated item by the same number would create a false destination. EveJS 261028.1 has dedicated providers for moon mining point, operation-site and dungeon-instance targets (`server/src/services/ship/beyonceService.js:3402-3843`); eve.rs has no equivalent wiring for them. A registered Host wire regression checks the refusal for each subject.
- `CmdTurboDock` remains absent because its only client caller is a client-side GM gate; the server has no verified authorization path for this privileged action. Ordinary docking still uses `CmdDock`. The difference is recorded in `docs/reference/compat/DEVIATIONS.md`.

## Checks

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` passed with zero warnings and errors.
- The focused ConfigService run passed 1 test. The Beyonce Host selection passed 10 tests and skipped `Beyonce_align_to_resolves_a_persisted_active_bookmark_through_the_bound_host_wire`; that skip is not counted as a pass. The separate focused Host selection passed 3 tests, including the config recordset wire regression. No selected test failed.
- Graphical V24.01 client behavior has not been exercised.
- Graphical V24.01 client behavior has not been exercised.
