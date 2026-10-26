# Activity, alpha injector, and bill manager RPC handoff

## Progress

- RPC-00834, RPC-00835, and RPC-00836 have a focused wire regression through the real `ServiceRegistry` and `PacketDispatcher`. It checks the default popup state, a state change and reread, character isolation, a wide activity ID wire value, and notification-before-reply ordering.
- RPC-00897–RPC-00898 have a Host wire regression for the no-character session response. The normal character-state and downtime behavior remains covered by existing Skills service tests.
- RPC-00930–RPC-00949 have a billMgr Host wire regression for empty corporation reads and refusal responses without corporation state. It does not cover a persisted corporation's successful bill/payment flow.
- The production `billMgr` registration in `CharacterServiceList.cs:71-77` omits `PayCharacterBillAsync`; `CharPayBill` therefore refuses every request. The task's IMPLEMENTED-UNTESTED classification does not match the production code.
- The bill state owner is corporation-only: `CorporationStore.cs:224` defines `corporation_bills.debtor_id` with a foreign key to `corporations(id)`. `CorporationStore.Alliance.cs:1140-1190` creates this table's bill entries for war declarations. Reusing that path for character bills would violate the data model. A correct personal bill store/payment flow remains unimplemented.
- All listed rows have been grouped by their unique method. Whole-client searches found only the cited callsites: the two activity methods and badge-status fetch, Alpha injection query, and the billMgr methods in walletSvc and the named corporation panels/war page. Duplicate row IDs represent multiple call contexts for the same method.

## V24.01 client callers and consumers

- `eve/client/script/ui/services/activitiesSvc.py:46-47`: loads `GetActivityBadgeStatuses()` into a set and compares activity IDs to calculate unseen status. `:174-181`: `GetActivitiesPopState()` feeds the login auto-popup check; `SetActivitiesPopState(state)` is followed by a truth check and local-state revert on false.
- `eve/client/script/ui/services/skillsvc.py:1004-1007`: consumes `GetNextAvailableInjection()` as a timestamp, calculating `max(0, next - wallclock)`.
- `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:125-130`: personal debtor calls `CharPayBill(billID)`; corporation debtor calls `PayCorporationBill(billID, fromAccountKey=...)` and scatters `OnCorpWalletBillsChanged`. `:55-59, 137-138`: handles `OnBillReceived` only for notification blinking and defines the corporation-bills refresh event.
- `corpBillsAutoPaidPanel.py:47-50,87-89`, `corpBillsPayablePanel.py:46-49,87-91`, `corpBillsReceivablePanel.py:74-76`: consume bill records and query debtor, bill type and paid status; the automatic-paid and payable panels also fetch settings. These panels subscribe to `OnCorpWalletBillsChanged` and repopulate at auto-paid `:28-29,81-83`, payable `:29-30,115-117`, receivable `:27-28,101-103`.
- `corpBillsAutoPaySettingsPanel.py:36-37,82-84`: reads and sends an owner-keyed settings map. `warLandingPage.py:184-185`: counts unpaid corporation war bills by type and paid status.

## EveJS comparison

- `/home/user/evejs-v0.12.9/server/src/services/activity/activityMgrService.js:100-109,128-145`: returns seen activity IDs, emits system-activity update notification, returns popup state, and returns a success value after setting it.
- `/home/user/evejs-v0.12.9/server/src/services/_other/alphaInjectorMgrService.js:15-20`: returns the next available injection time.
- `/home/user/evejs-v0.12.9/server/src/services/account/billMgrService.js:202-316`: returns bill lists/settings and null after successful payment. `:305-316` handles personal bills via `payBillFromCharacter`; `billRuntimeState.js:709-780,782-784` validates and settles through EveJS's durable bill registry. This state owner has no equivalent in eve.rs.

## Checks

- Local activity Host test: 1 passed, 0 failed, 0 skipped.
- Local alpha injector Host test: 1 passed, 0 failed, 0 skipped.
- Local billMgr Host test: 1 passed, 0 failed, 0 skipped.
- Activity Actions run 40264834187 for revision 60a532ea: passed, 1 passed, 0 failed, 0 skipped.
- billMgr Actions run 40266992468 for revision 1921e8ef: passed, 1 passed, 0 failed, 0 skipped.
- Alpha Actions run 40268456043 for revision 390e91a7: passed, 1 passed, 0 failed, 0 skipped. Push-triggered ci.yml run 40268448381 for 390e91a7 is still in progress.
- No database connection was used.
- Real graphical-client verification has not been performed.
