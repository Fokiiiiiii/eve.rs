# Activity, alpha injector, and bill manager RPC handoff

## Progress

RPC-00834, RPC-00835, and RPC-00836 now have a focused wire regression through the real `ServiceRegistry` and `PacketDispatcher`. It checks the default popup state, a persisted-in-service state change, character isolation, the wide activity ID wire value, and notification-before-reply ordering. No service code changed.

The remaining rows in this batch are RPC-00897–RPC-00898 and RPC-00930–RPC-00949.

## V24.01 client evidence

- `eve/client/script/ui/services/activitiesSvc.py:46-47`: loads `GetActivityBadgeStatuses()` into a set; the activity list compares each activity ID with that set when calculating unseen status.
- `eve/client/script/ui/services/activitiesSvc.py:174-181`: `GetActivitiesPopState()` feeds the login auto-popup check; `SetActivitiesPopState(state)` is followed by a truth check and a local state revert on false.
- `eve/client/script/ui/services/skillsvc.py:1004-1007`: `GetNextAvailableInjection()` returns a timestamp consumed as `max(0, next - wallclock)`.
- `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:125-130`: personal bill payments call `CharPayBill(billID)`; corporation payments pass `fromAccountKey=session.corpAccountKey` and then trigger `OnCorpWalletBillsChanged`.
- `eve/client/script/ui/shared/neocom/wallet/panels/corp/corpBillsAutoPaidPanel.py:47-50, 87-89`, `corpBillsPayablePanel.py:46-49, 87-91`, and `corpBillsReceivablePanel.py:74-76`: consume bill lists as records with `debtorID`, `billTypeID`, and `paid`, and automatic settings as an owner-keyed mapping.
- `eve/client/script/ui/shared/neocom/wallet/panels/corp/corpBillsAutoPaySettingsPanel.py:36-37, 82-84`: reads the owner-keyed setting map and submits the edited map.
- `eve/client/script/ui/shared/neocom/corporation/war/warLandingPage.py:184-185`: counts unpaid corporation war bills by `billTypeID` and `paid`.

## EveJS comparison

- `/home/user/evejs-v0.12.9/server/src/services/activity/activityMgrService.js:100-109, 128-145`: returns the seen activity ID list, emits the system-activity update notification, returns popup state, and returns a success value after setting it.
- `/home/user/evejs-v0.12.9/server/src/services/_other/alphaInjectorMgrService.js:15-20`: returns the next available injection time.
- `/home/user/evejs-v0.12.9/server/src/services/account/billMgrService.js:202-316`: returns keyed settings and bill records, persists submitted settings, and returns null after successful payment.

## Checks

- Local: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~Activity_mgr_calls_route_through_host_wire_and_preserve_character_state' -p:UseSharedCompilation=false` — 1 passed, 0 failed, 0 skipped.
- No database connection was used.
- Real graphical-client verification has not been performed.
