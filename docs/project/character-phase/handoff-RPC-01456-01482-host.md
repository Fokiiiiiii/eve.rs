# Host RPC coverage handoff: RPC-01456–RPC-01482

## Result

Added `Ess_epic_arc_and_wreck_rpcs_are_dispatched_with_client_shapes` in `dotnet/tests/Eve.Host.Tests/EssRpcHostWireTests.cs`. It uses `HostServerTests.Start`, the real `ServiceRegistry` and `PacketDispatcher`, a loopback Host socket, and the registered service objects. It checks the ESS detail and agency dictionaries, current-system ESS data, reserve-link state, theft lists, null replies and operation requests for link/unlink/unlock, Epic Arc bool replies, and eject argument dispatch/null replies.

The production ESS services were already registered from `CoreRpc.cs` and composed with `PgEssStore` in `EssComposition.cs`; no duplicate routes or state store were added. The test uses in-memory callback state and does not touch PostgreSQL.

## Row disposition

- RPC-01456: `GetESSSystemDetails` reply shape covered.
- RPC-01457–01458: client-local `dict.iteritems()` calls on `GetESSAgencyData()`; no remote method exists to register.
- RPC-01459–01460: `LaunchShipOrContainerFromWreckOrContainer` wire args and null reply covered.
- RPC-01463–01465: `AgentHasEpicMissionsForCharacter` bool wire reply covered for two agent IDs.
- RPC-01466–01479: existing `essMgr` routes covered for data, link status, theft lists, link/unlink requests, and reserve unlock.
- RPC-01480–01482: client-local memoizer `.clear_cache()` calls; no remote method exists to register.

## Client and EveJS evidence

- `eve/client/script/ui/shared/agencyNew/contentPieces/essSystemContentPiece.py:19-21` calls `GetESSSystemDetails(solarSystemID)`; `eve/client/script/ui/shared/agencyNew/essSystemInfoContainer.py:46` consumes its result.
- `eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderESS.py:38-39` iterates the returned agency-data dictionary locally; its remote `GetESSAgencyData()` call is at line 55 and the consumed keys are at lines 11-14.
- `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:41-44` calls eject; menu callers are at lines 3464 and 3468.
- `eve/client/script/ui/station/agents/agentUtil.py:39-40,64-65` and `eve/client/script/ui/shared/dockedUI/agents.py:193-194` branch on the Epic Arc bool.
- `eve/client/script/ui/shared/dynamicResource/dynamicResourceSvc.py:72-77,122-152` calls the ESS query and mutation methods; lines 179, 189, and 195 clear local cached state. The system-change handler primes local ESS data and clears the local access-history memoizer.
- EveJS v0.12.9 returns ESS data at `server/src/services/dynamic/essMgrService.js:204-222`, returns null for link/unlink/unlock calls while publishing success notifications at lines 224-322, and supplies theft lists at lines 324-344. Agency details are built by `dynamicResourceCacheMgrService.js:32-38`. Eject returns null after dispatch at `server/src/services/ship/ejectService.js:16-30`; Epic Arc returns a bool at `server/src/services/agent/epicArcStatusService.js:238-243`.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:BuildProjectReferences=false -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- Focused Host wire test: 1 passed, 0 failed, 0 skipped.
- No database or graphical-client run. The wire fixture does not exercise PostgreSQL persistence or notification fan-out; those production paths remain covered by their existing service/Host tests.
- The branch was merged with the fetched `origin/main` at merge commit `a5c78ab0`; the merged Host test project builds cleanly and the focused test passes again.
- Pushed to `main` at `057404f1e36b4b0d7e5d79250f1a19702c74dffc`.
- Selected Actions run `40200598714` passed (1 targeted test, 0 failures, 0 skips): https://github.com/Fokiiiiiii/eve.rs/actions/runs/40200598714.
- Push-triggered `ci.yml` run `40200591142` failed at `.NET (space tests 13/32) → Upload test timing results`; the failure summary contained no failed test name, and the step log ended after artifact path validation. The selected Host wire run and the other listed CI jobs passed. This points to the artifact upload step rather than test assertions, but the repository status output does not expose the underlying cause.
