# Corporation service audit findings and fixes

Audit source: `/home/user/reports/90-audit-eve-services-corporations-r14.md`. I checked each item against the V24.01 client source in `/home/user/eve-logic`, EveJS v0.12.9 in `/home/user/evejs-261028.1`, and the current Host/store wiring before changing code.

## P1 — warRegistry negotiations: confirmed and fixed

The client calls `GetNegotiations`, `GetWarNegotiation`, ally offer creation/retraction/acceptance/decline, and surrender creation/acceptance/decline through `war_cso.py:208-253`. The negotiation windows issue those actions from `warWindows.py:243-265, 319-333`; the wars list reads pending ally offers in `warEntry.py:106` and surrender offers in `warEntry.py:218`.

EveJS implements the RPCs in `server/src/services/corporation/warRegistryService.js:148-186, 189-253, 256-290, 312-346`. Its persistent negotiation records and states are in `warNegotiationRuntimeState.js:147-190`; acceptance charges the defender's selected wallet division, includes the calculated CONCORD fee when allies already exist, starts an ally after four hours, and limits its term to the earlier of fourteen days or the war end (`:350-405`). Surrender charges the offerer's stored division, ends the war, and creates two fourteen-day peace treaties with reason 4 (`:407-447, 487-514`). These are the behaviors implemented in eve.rs.

`WarNegotiationStore` now persists offers, transitions, and peace treaties in the corporation schema. The Host registers it with wallet and authority providers. Wallet settlement, negotiation acceptance, ally relations or war end, and surrender treaties commit in the same PostgreSQL transaction. Persistent war notifications use the existing notification writer; `OnWarChanged` is sent after the RPC response.

Regression coverage exercises list/detail wire fields, restart reads, director rejection, offer retraction and declines, unpayable offers, concurrent/retried acceptance without duplicate transfer, four-hour ally start, fourteen-day expiry, surrender reason and treaty rows. A real Host integration test sends Macho requests through the registered `warRegistry`, verifies recipient notices and RPC/`OnWarChanged` order, checks wallet/relation persistence, then reads the treaties through the registered `peaceTreatyMgr`.

## P1 — `CharPayBill`: skipped as unconfirmed in the normal client flow

The callback is indeed unset in `CharacterServiceList.cs:71-76`, and `BillMgrService.cs:42-49` rejects `CharPayBill` without it. The client contains a generic branch in `walletSvc.py:125-127`. However, tracing all V24.01 wallet UI callers shows the payable panel obtains bills only for the corporation and alliance (`corpBillsPayablePanel.py:69-92`); the only client `CharPayBill` call is that generic branch. The EveJS bill producers are all in corporation services: alliance maintenance (`allianceMaintenanceBilling.js:194-204`), wars (`warRuntimeState.js:226-234`, `corpRegistryRuntime.js:3875-3883`, `allianceRegistryRuntime.js:1227-1235`, `allianceWarInheritance.js:224-234`), and office rent (`officeRentalBilling.js:251-263, 287-299`). Their debtors are corporations or alliances. No EveJS producer creates a character-debtor bill in the checked source tree.

EveJS has a generic character-payment routine (`billMgrService.js:314-324`, `billRuntimeState.js:783-785`), but that alone does not establish a normal V24.01 path that creates or presents a personal debtor bill. I therefore skipped the proposed individual-bill backend instead of adding a speculative second bill store. The existing `CharPayBill` call would still fail if a character-debtor record were inserted by some external producer; production generation of such records and a graphical-client scenario were not verified.

## P2 — office rent/unrent bill refresh: confirmed and fixed

V24.01 `walletSvc.py:56-62` refreshes the corporation bill list on `OnBillReceived`. EveJS `officeManagerService.js:513-516, 572-579, 672-683` sends that notice after a successful rent or unrent response. The eve.rs office paths now schedule it after successful rent, after an unrent that actually cancels an open bill, and after rent-due eviction when an open bill is cancelled. Rent/unrent no-ops do not send a bill refresh.

The Host regression checks the actual `officeManager` service, bill open/cancelled state, and that `OnBillReceived` follows the RPC response for rent and unrent.

## Verification limits

The client and EveJS behavior was cross-checked by local source inspection with file/line references above. The EveJS repository has no focused war-negotiation test file or runner in the checked tree, so I did not execute its server flow. Tests against eve.rs use the local `eve_p4` PostgreSQL database and Host RPC dispatcher; the graphical V24.01 client was not launched.

## Checks run

Release builds with warnings treated as errors succeeded for `Eve.Corporations`, `Eve.Services.Corporations`, `Eve.Host`, `Eve.Corporations.Tests`, and `Eve.Host.Tests` (0 warnings, 0 errors). The filtered Release corporation slice passed 46 tests; the related Release Host slice passed 17 tests. Both used `eve_p4`; neither reported skipped tests. The same focused slices had also passed in Debug. No full test suite or live graphical-client session was run.
