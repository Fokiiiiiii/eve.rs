# charMgr.GetPublicInfo Host coverage

## Scope and result

RPC-01006 through RPC-01030 are distinct client call and response observations for the same read-only method. The production path is registered by `CharacterRpc.cs:73` and reads the authoritative `PgCharacterStore` row, then description, security status and medal providers. The method returns the existing 24-field `KeyVal`; a read has no persistence or notification side effect.

`CharMgrService` now accepts an optional character-row reader for deterministic Host tests. Production composition omits it and continues to use `PgCharacterStore.GetAsync`. The new `Eve.Host.Tests` case registers the actual service in the socket Host and exercises the wire with the transport `machoVersion` keyword, two different character rows, and a missing ID. It asserts the complete ordered record and client-used values.

## V24.01 callers and consumers

A whole-tree `GetPublicInfo(` search found these `charMgr` callers:

- `avatardisplay/avatardisplay.py:1259-1260,1359-1360` reads `bloodlineID`.
- `eve/client/script/ui/services/ccSvc.py:179-181` reads `schoolID`, then uses school corporation data.
- `eve/client/script/ui/services/crimewatchSvc.py:179-187` copies corporation, war-faction and alliance values into a slim item.
- `evephotosvc/scenePortraitQueue.py:125-127` caches the record and maps `UserError` to `None`; `evephotosvc/scenePortraitRenderer.py:188-204` uses its `gender` and `bloodlineID`.
- `operations/client/UI/operationDestinationController.py:12-14` reads `schoolID`.
- `eve/client/script/ui/shared/info/panels/panelStandings.py:120-126` reads `corporationID` and uses its alliance.
- `eve/client/script/ui/shared/preview.py:851-859` requires `gender` and passes the record to character scene setup.
- `eve/client/script/ui/shared/neocom/charsheet/characterOverviewPanel/characterOverviewPanel.py:46,90-93,141` reads `raceID`, `bloodlineID`, `ancestryID`, `schoolID` and `createDateTime`.
- `eve/client/script/ui/station/agents/agents.py:281-284,519-522` reads `schoolID` to find school and Aura agents.
- `nodegraph/client/conditions/safe_schools_client.py:12-20` handles a missing record or `schoolID` as false.
- `nodegraph/client/getters/location.py:31-33` maps `schoolID` to a birth station.
- `homestation/client/service.py:116-119` reads `schoolID` and resolves its corporation station.
- `safespace/client/util.py:8-20` treats a missing record or `schoolID` as false.

The same whole-tree search also found `corpmgr.GetPublicInfo` in `killRights.py:151`, `bountyWindow.py:410`, `corporationInfoWindow.py:48,183`, and `warPermit.py:42`; those are different RPCs and are outside this batch. `GetPublicInfo3` callers are separate and were not changed.

## EveJS comparison

EveJS v0.12.9 `server/src/services/character/charMgrService.js:372-421` constructs the same ordered 24-field public record, and `526-530` returns it as a KeyVal. The client uses the fields named above. EveJS returns a default-filled record for unknown IDs via `resolveCharacterInfo` at `82-90`; eve.rs returns `None` for a missing character. This existing behavior difference is recorded in `docs/reference/compat/DEVIATIONS.md:185` and the wire test preserves it.

## Checks

- The changed `Eve.Services.CharacterInfo` project compiled successfully during the Host test project build.
- The full Host test project build was canceled after 1 hour 26 minutes because the shared compiler queue remained saturated; it reported no source errors before cancellation.
- No PostgreSQL database was used.
- Focused Host test and GitHub Actions results: pending.
- Graphical-client verification was not performed.
