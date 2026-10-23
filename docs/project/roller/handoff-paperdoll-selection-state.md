# Paperdoll selection state handoff

## Scope

charUnboundMgr.GetCharacterSelectionData projects paperdollState from the PostgreSQL character row that owns appearance and recustomization. The four-element selection tuple and util.KeyVal rows are preserved.

PgCharacterStore.ResolvedPaperdollStateAsync reads paperdoll_state, doll_info, and portrait_info together. A persisted state is returned as stored; otherwise appearance yields 0 and missing appearance yields 2. CharMgrService.GetPaperdollState uses this reader when its optional recustomization reader has no value. SetAppearanceAsync clears the persisted forced state in the same update.

Host composition resolves the InvBrokerService through IndustryInventoryChangePublisher when wiring RepackagingService.

## Evidence

- V24.01 client route: /home/user/eve-logic/eve/client/script/ui/login/charSelection/characterSelection.py:727-740; states 2 and 4 call CreateNewAvatar.
- V24.01 state values: /home/user/eve-logic/paperdoll/common/state.py:8-17.
- Client row accessor: /home/user/eve-logic/loginCharacterselection/charselData.py:26-42,170-171.
- EveJS selection projection: /home/user/evejs-261018.1/src/server/src/services/character/charService.js:857-981; paperDollPayloads.js:399-406.
- EveJS save path: /home/user/evejs-261018.1/src/server/src/services/character/paperDollServerService.js:229-258.
- Persistent owner and reset: dotnet/src/Eve.Characters/Characters.cs, PgCharacterStore.SetAppearanceAsync.

## Regression coverage

CharacterReSculptHostTests.Character_selection_dispatch_projects_paperdoll_state_and_a_saved_avatar_can_enter checks the client wire shape, 0/2/4, retry, Host restart, save reset, and SelectCharacterID. The test now bootstraps Host migrations before inserting its fixture characters. The live headless test test_paperdoll_selection.py walks login, selection state 0, missing-appearance state 2, appearance save, and game entry.

## Verification

Full-SHA Host dispatch run 39960126401 compiled and ran the focused test, which failed because its fixture characters were inserted before Host created the characters table. That setup order is fixed. Headless run 39960167925 is still in progress. The graphical-client route has not been exercised.
