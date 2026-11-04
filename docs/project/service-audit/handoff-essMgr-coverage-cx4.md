# essMgr coverage handoff (cx4)

## Result

RPC-01421 through RPC-01434 now have active Host coverage and are recorded as IMPLEMENTED-TESTED in `EVE_RS_COVERAGE.csv`. Host production composition registers `EssMgrService`; `PgEssStore` owns bank and link state. The mutating RPCs now queue client notifications with `ClientSession.AfterResponse`, so the reply precedes the link/disconnect and state-change notifications.

The PostgreSQL Host test exercises registered wire calls for both banks, persisted main/reserve link state, reserve key unlock state, exact main-link fields and FILETIME values, list and boolean reply shapes, disconnect payloads, and notification order. The existing Host wire test also checks non-empty theft list shapes.

## Client evidence

- `/home/user/eve-logic/eve/client/script/ui/shared/dynamicResource/dynamicResourceSvc.py:69-152` contains all ESS RPC call wrappers: state, reserve link status, main/reserve link and unlink, reserve unlock, and both theft histories.
- `/home/user/eve-logic/eve/client/script/ui/shared/dynamicResource/dynamicResourceSvc.py:166-173` consumes `OnSystemESSDataChanged(systemID, data)` and refreshes system data/history.
- `/home/user/eve-logic/eve/client/script/ui/shared/dynamicResource/dynamicResourceSvc.py:192-230` consumes the main/reserve link notifications and main/reserve disconnect notifications.
- `/home/user/eve-logic/eve/client/script/ui/shared/dynamicResource/dynamicResourceSvc.py:258-268` consumes `mainBankLink`, reserve pulse counts, active links, and bank values; it converts main-link times for UI use.
- `/home/user/eve-logic/eve/client/script/ui/shared/infoPanels/essUI/controller.py:51-110` reads linked character and bank/pulse state for the ESS panel.
- `/home/user/eve-logic/eve/client/script/ui/shared/infoPanels/essUI/infoPanelESS.py:43-44,107,227-257,290-291,353-393,416-417,483,488` subscribes to ESS changes and renders values, links, payouts, and link/disconnect events.

## Verification

Passed locally on database `eve_p4` through `tools/dev/db-lock.sh`:
- `Eve.Host.Tests.Ess_main_bank_calls_use_host_wire_and_persist_state_with_client_notifications`
- `Eve.Host.Tests.Ess_epic_arc_and_wreck_rpcs_are_dispatched_with_client_shapes`
- `Eve.Host.Tests.Dynamic_resource_cache_reads_are_returned_through_host_wire`
- `Eve.Services.Space.Tests.EssMgrServiceTests` and `PgEssStoreTests` (15 tests total)

The client source, existing Host route registration, production store wiring, and EveJS current implementation were inspected. No gameplay deviation from EveJS or the client contract was introduced. The graphical EVE client was not used.

## Open rows

None among RPC-01421 through RPC-01434. Other service audit rows are outside this handoff.
