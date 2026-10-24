# Station-entry RPC budget handoff

## Change
PacketDispatcher now charges contractProxy and corpRegistry/corporation against their own rolling-minute budgets, independent of the shared general RPC pool. The station alias and canonical service name share the existing station budget. No handler or reply construction changed.

## Client evidence
- invCache.GetItemsInCurrentStationShipHangar and GetItemsInCurrentStationItemHangar call GetInventory (eve/client/script/environment/invCache.py:482-489); GetInventory obtains the inventory proxy (501-531), and invCacheContainer.List reads moniker rows and caches them (1131-1165).
- Dogma GetAllInfo is consumed as character/ship state by clientDogmaIM (eve/client/script/dogma/clientDogmaIM.py:50-55) and godma.Prime (eve/client/script/environment/godma.py:2401-2410).
- station.GetGuests is called by StationSvc.IsGuest and becomes a character-to-affiliation map (eve/client/script/ui/station/base.py:98-113); the docked guest UI also fetches it and constructs Guest records (eve/client/script/ui/shared/dockedUI/guests.py:93-100).
- contractProxy.GetLoginInfo consumers read assignedToMe, needsAttention, and inProgress (eve/client/script/ui/shared/neocom/contracts/contracts.py:190-209).
- Corporation application calls go through the corp-registry wrapper (eve/client/script/ui/services/corporation/base_corporation.py:558-563). bco_applications caches the corp-keyed mapping, status-filters it, and updates it on application-change notifications (eve/client/script/ui/services/corporation/bco_applications.py:39-42, 69-92).
- The station hangar walk now explicitly sends GetInventory(10004, None), asserts a handle, and lists the returned inventory (tools/headless-client/tests/test_cargo.py:45-48).
- EveJS v0.12.9 has the corresponding inventory bind (server/src/services/inventory/invBrokerService.js:13617-13728), station guest read (server/src/services/station/stationService.js:134-154), and corp application read (server/src/services/corporation/corpRegistryRuntime.js:2287-2300). Its dispatcher has no SessionRpcBudgetExceeded gate.

## Checks
- Focused dispatcher regression: 1 passed, 0 skipped. It fills the general allowance with weighted RPCs, replays the six station-entry routes at default limits, then confirms 17 contract reads in one minute are refused after the first 16.
- test_cargo.py Python syntax compilation passed.
- Headless cargo walk and graphical-client verification remain outstanding; no PostgreSQL database was used.
- The original /home/user/wt-cx12 tree had staged/conflicted Skyhook work. It remains untouched; implementation is isolated in /home/user/wt-cx12-rpcbudget.

## Push / Actions
- Implementation commit `af9cd4029eaead6064f7c95cb2e3e91a309cd812` was pushed after fetching and merging `origin/main` (then already up to date).
- Focused Actions run 40069789991 passed with no failed tests or jobs.
- The push-triggered `ci.yml` run 40069784822 was cancelled when a later handoff update was pushed; its required gate reported `.NET (space tests 2/2)` cancelled, while the other jobs succeeded.
- At `3f7ab3de`, after merging the then-current `origin/main`, the local focused test passed (1 passed, 0 skipped); focused Actions run 40072174572 passed with no failed tests or jobs.
- `ci-status.py failures 3f7ab3de` reported no push-triggered `ci.yml` run for that merge commit.