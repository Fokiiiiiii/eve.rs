# charUnboundMgr Host coverage handoff

## Current status

This batch covers all eight target P0 rows: RPC-01095/01096 CreateCharacterWithDoll, RPC-01099/01100 GetCharacterInfo, RPC-01121/01122 UpdateCharacterBloodline, and RPC-01123/01124 UpdateCharacterGender. Their CSV statuses now reference the real Host test `CharUnboundMgrDemographicsHostWireTests.Character_creation_info_and_demographic_updates_round_trip_over_registered_host_wire`.

The test registers the production service from `CharacterHostRig` in the Host registry and sends Macho calls through the socket and marshal codec. It creates a character with doll and portrait data, checks the persisted appearance values, reads the character through GetCharacterInfo, updates bloodline and gender through the wire, checks session and selection data, and checks the stored demographics after a Host restart. The RPCs return None for demographic updates; the client performs its own session/cache refresh work. No handler defect was exposed.

## Whole-client evidence

Searches across the V24.01 `home-user-eve-logic` project found:

| RPC | Callers and consumers |
|---|---|
| CreateCharacterWithDoll | `eve/client/script/ui/services/ccSvc.py:91-98` forwards the ten arguments, clears selection data, and returns the character ID. The old creation flow calls it at `characterCreationContainer.py:1839` then calls photo.AddPortrait with that ID at 1847. The new flow does the same at `characterCreationContainer_new.py:1300,1311`. |
| GetCharacterInfo | `eve/client/script/ui/shared/neocom/petitionWindow.py:57` calls it; line 62 consumes each row's `characterID` and `characterName` to construct the OOC character checkbox. |
| UpdateCharacterGender | `eve/client/script/ui/services/ccSvc.py:115-123` calls it, updates the client session, updates the paperdoll, clears the current paperdoll cache, invalidates public-info cache, and refreshes character selection. |
| UpdateCharacterBloodline | `eve/client/script/ui/services/ccSvc.py:135-142` calls it, optionally clears paperdoll data and refreshes selection, then updates the client session bloodline. |

The wire and persistence behavior was compared with current EveJS 261028.1 at `/home/user/evejs-261028.1/src/server/src/services/character/charService.js`: CreateCharacterWithDoll 1089-1373, UpdateCharacterGender 1384-1415, UpdateCharacterBloodline 1417-1462, and GetCharacterInfo delegation 1521-1524. The tested shapes and state changes match those handlers and the client call flow.

## Verification

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore`: passed, 0 warnings/errors.
- Host DB regression set on `eve_p3` through `tools/dev/db-lock.sh`: 31 passed, 0 failed, 0 skipped (including this test, all CharUnbound Host tests, P0 client wire tests, character deletion, and resculpt host tests).
- `dotnet build dotnet/tests/Eve.Services.CharacterSelect.Tests/Eve.Services.CharacterSelect.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- CharacterSelect service regression set on `eve_p3` through `tools/dev/db-lock.sh`: 35 passed, 0 failed, 0 skipped.
- The other P0 rows targeted by this batch are none. The graphical EVE client was not exercised.
- Commit `cc279a6c38b98c3304c761a3e08a946954676a80` is on `main`. Selected Actions run `41069412049` passed the focused Host test; push-triggered CI run `41069400625` passed with no failed tests or jobs.
