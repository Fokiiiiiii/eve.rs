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

## Follow-on: selection wars, faction and wallet delta

`GetCharacterSelectionData` keeps the four-tuple and now emits active `war` / `war.ally` relation rows grouped with each `characterID`. The row's `factionID` comes from the Host's existing `WarFactionOfAsync` path, which reads direct enlistment or active corporation/alliance enlistment; base faction and school do not reach the client. `balance` and latest ISK journal delta are read in one wallet query, with the delta sent as `PyReal`. `GetCharacterInfo` shares the same wallet and militia row projection.

Whole-client evidence: `loginCharacterselection/charselData.py:30-37` groups war rows by `characterID`; `charselData.py:72-89` makes synthetic faction-enemy wars from truthy `factionID`; `charselData.py:174-180` consumes `balanceChange` and wars; `characterSlots.py:476-490` draws the wallet change; `characterSlots.py:497,739-745` handles war display and loads the owner tooltip; `atWarTooltip.py:32-47` reads the war sides, mutual flag and ally count.

EveJS 261028.1 `charService.js:857-981` returns an empty wars list and copies its wallet state's `balanceChange`; `walletState.js:649-666` writes each delta with its journal entry. The V24 consumers plus eve.rs's persisted relation and wallet state justify the selection projection difference. EVE University [Corporation diplomacy](https://wiki.eveuniversity.org/Corporation_diplomacy) gives the war startup/cooldown and ally-start timing used by the active-war filter.

## Selection projection verification (2026-11-06)

- Built `Eve.Wallet`, `Eve.Services.CharacterSelect`, `Eve.Services.Corporations`, `Eve.Host`, and the CharacterSelect, Corporations, Host, Wallet, and Wallet service test projects with shared compilation and node reuse disabled; all builds passed with 0 warnings and 0 errors.
- On the task database `eve_p7`, with the pinned SDE schemas copied from `eve_p3`, `CharUnboundMgrTests` passed 36/36, `CorporationProtocolTests` passed 55/55, the selected Host wire/registration set passed 411/411, `Eve.Wallet.Tests` passed 43/43, and `Eve.Services.Wallet.Tests` passed 16/16. All used `tools/dev/db-lock.sh`; no tests were skipped.
- The first CharacterSelect run exposed that the default `eve` database in this cluster has no SDE schemas. The run passed after preparing `eve_p7` from `eve_p3` using `tools/dev/copy-sde-database.py`.
- Selected agent-test run [42198229168](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42198229168) and push-triggered CI run [42198159732](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42198159732) passed on commit `bdce763e`; neither reported failed tests or jobs.
- The V24 graphical client was not exercised.

## Previous verification

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore`: passed, 0 warnings/errors.
- Host DB regression set on `eve_p3` through `tools/dev/db-lock.sh`: 31 passed, 0 failed, 0 skipped (including this test, all CharUnbound Host tests, P0 client wire tests, character deletion, and resculpt host tests).
- `dotnet build dotnet/tests/Eve.Services.CharacterSelect.Tests/Eve.Services.CharacterSelect.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- CharacterSelect service regression set on `eve_p3` through `tools/dev/db-lock.sh`: 35 passed, 0 failed, 0 skipped.
- The other P0 rows targeted by this batch are none. The graphical EVE client was not exercised.
- Commit `cc279a6c38b98c3304c761a3e08a946954676a80` is on `main`. Selected Actions run `41069412049` passed the focused Host test; push-triggered CI run `41069400625` passed with no failed tests or jobs.
