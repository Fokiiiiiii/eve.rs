# Paperdoll selection state handoff

## Scope

charUnboundMgr.GetCharacterSelectionData projects paperdollState from the PostgreSQL character row that owns appearance and recustomization. The four-element selection tuple and util.KeyVal rows are preserved.

PgCharacterStore.ResolvedPaperdollStateAsync reads paperdoll_state, doll_info, and portrait_info together. A persisted state is returned as stored; otherwise appearance yields 0 and missing appearance yields 2. CharMgrService.GetPaperdollState uses this reader when its optional recustomization reader has no value. SetAppearanceAsync clears the persisted forced state in the same update.

Migration 0015 expands the database constraint to admit forced state 4 alongside existing recustomization states 1 and 3. States 0 and 2 are derived from appearance data.

Host composition builds RepackagingService from the raw InvBrokerService, then wraps registered brokers with IndustryInventoryChangePublisher. This preserves the broker dependency after the inventory publisher moved to the final service-list wiring stage.

## Evidence

- V24.01 client route: /home/user/eve-logic/eve/client/script/ui/login/charSelection/characterSelection.py:727-740; states 2 and 4 call CreateNewAvatar.
- V24.01 state values: /home/user/eve-logic/paperdoll/common/state.py:8-17.
- Client row accessor: /home/user/eve-logic/loginCharacterselection/charselData.py:26-42,170-171.
- EveJS selection projection: /home/user/evejs-261018.1/src/server/src/services/character/charService.js:857-981; paperDollPayloads.js:399-406.
- EveJS save path: /home/user/evejs-261018.1/src/server/src/services/character/paperDollServerService.js:229-258.
- Persistent owner and reset: dotnet/src/Eve.Characters/Characters.cs, PgCharacterStore.SetAppearanceAsync.

## Regression coverage

CharacterReSculptHostTests.Character_selection_dispatch_projects_paperdoll_state_and_a_saved_avatar_can_enter checks tuple and row wire shape, states 0/2/4, retry, restart persistence, save reset, and SelectCharacterID. Host migrations run before fixture inserts. Headless test test_paperdoll_selection.py checks state 0, missing-appearance state 2, saved-appearance state 0, and game entry.

## Verification

Host dispatch run 39964185288 passed; focused headless run 39965636111 passed with 1 passed, 0 failed, and 0 skipped. Push CI run 39965909645 failed because the current main service composition moved the inventory broker wrapper after RepackagingService registration; the composition lookup is corrected to pass the raw broker and is pending focused Actions verification. The graphical-client route has not been exercised.
