# Paperdoll selection state handoff

## Scope

charUnboundMgr.GetCharacterSelectionData projects paperdollState from the PostgreSQL character row that owns appearance and recustomization. The four-element selection tuple and util.KeyVal rows are preserved.

PgCharacterStore.ResolvedPaperdollStateAsync reads paperdoll_state, doll_info, and portrait_info together. A persisted state is returned as stored; otherwise appearance yields 0 and missing appearance yields 2. CharMgrService.GetPaperdollState uses this reader when its optional recustomization reader has no value. SetAppearanceAsync clears the persisted forced state in the same update.

Migration 0015 expands the database constraint to admit forced state 4 alongside existing recustomization states 1 and 3. States 0 and 2 are derived from appearance data.

Host composition builds RepackagingService from the raw InvBrokerService, then wraps registered brokers with IndustryInventoryChangePublisher. This preserves the dependency after the publisher wiring moved to the final service-list stage.

## Evidence

- V24.01 client route: /home/user/eve-logic/eve/client/script/ui/login/charSelection/characterSelection.py:727-740; states 2 and 4 call CreateNewAvatar.
- V24.01 state values: /home/user/eve-logic/paperdoll/common/state.py:8-17.
- Client row accessor: /home/user/eve-logic/loginCharacterselection/charselData.py:26-42,170-171.
- EveJS selection projection: /home/user/evejs-261018.1/src/server/src/services/character/charService.js:857-981; paperDollPayloads.js:399-406.
- EveJS save path: /home/user/evejs-261018.1/src/server/src/services/character/paperDollServerService.js:229-258.
- Persistent owner and reset: dotnet/src/Eve.Characters/Characters.cs, PgCharacterStore.SetAppearanceAsync.

## Regression coverage

CharacterReSculptHostTests.Character_selection_dispatch_projects_paperdoll_state_and_a_saved_avatar_can_enter checks tuple/row wire shape, states 0/2/4, retry, restart persistence, save reset, and SelectCharacterID. Host migrations run before fixture inserts. Headless test test_paperdoll_selection.py checks state 0, missing-appearance state 2, saved-appearance state 0, then game entry.

## Verification

Host dispatch run 39967232007 passed. Focused headless run 39968263931 passed with 1 passed, 0 failed, and 0 skipped. Both ran against commit 1f08f9cbe939c8c2297265036d11e8adb69b05ef. Push-triggered CI run 39967190561 passed .NET build and other reported jobs but failed the unrelated test_market_browse_and_history_refresh_fit_one_session at marketProxy.GetMarketOrderHistory with Macho error 2. The graphical-client route has not been exercised. Local .NET restore/build remained stalled; no PostgreSQL database was used locally.
