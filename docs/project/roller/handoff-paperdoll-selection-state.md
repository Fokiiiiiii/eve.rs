# Paperdoll selection state handoff

## Scope

charUnboundMgr.GetCharacterSelectionData now projects the client-facing paperdoll state from the same PostgreSQL character row that owns appearance and recustomization. The selection tuple remains four elements, and each character row remains a util.KeyVal.

PgCharacterStore.ResolvedPaperdollStateAsync reads paperdoll_state, doll_info, and portrait_info together. A persisted state is returned as stored; otherwise an appearance payload yields 0 and missing appearance yields 2. CharMgrService.GetPaperdollState uses this reader when the optional recustomization reader has no value. The paperdoll save RPC persists the appearance through SetAppearanceAsync, which clears the persisted forced state in the same update.

## Evidence

- V24.01 client route: /home/user/eve-logic/eve/client/script/ui/login/charSelection/characterSelection.py:727-740; states 2 and 4 call CreateNewAvatar.
- V24.01 state values: /home/user/eve-logic/paperdoll/common/state.py:8-17.
- Client row accessor: /home/user/eve-logic/loginCharacterselection/charselData.py:26-42,170-171.
- EveJS selection projection: /home/user/evejs-261018.1/src/server/src/services/character/charService.js:857-981; paperDollPayloads.js:399-406 resolves stored appearance and the fallback state.
- EveJS save path: /home/user/evejs-261018.1/src/server/src/services/character/paperDollServerService.js:229-258.
- Persistent owner and reset: dotnet/src/Eve.Characters/Characters.cs, PgCharacterStore.SetAppearanceAsync.

## Regression coverage

CharacterReSculptHostTests.Character_selection_dispatch_projects_paperdoll_state_and_a_saved_avatar_can_enter dispatches through Host and checks tuple/row wire types, states 0/2/4, repeat reads, restart persistence, save reset, and the game-entry transition after saving the forced character. The live headless test test_paperdoll_selection.py walks login, selection state 0, missing-appearance state 2, appearance save, and game entry.

## Verification

The Host test is database-backed and must run through the selected GitHub Actions job. The graphical-client route has not been exercised; record that check separately from the headless/Host result.
