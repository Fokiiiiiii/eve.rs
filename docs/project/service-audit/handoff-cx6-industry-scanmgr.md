# Service audit handoff: cx6

## Completed in this increment

The production implementation and Host registration were already active in the starting revision; this increment adds the missing full Host wire regression and corrects the stale MISSING audit rows. Industry monitor rows RPC-01585–RPC-01588 are covered by the registered Host wire test Industry_monitor_connect_disconnect_and_material_notice_cross_the_registered_host_wire in dotnet/tests/Eve.Host.Tests/IndustryMonitorHostWireTests.cs. It asserts the ConnectJob two-item tuple with a type-ID-to-quantity dictionary, OnIndustryMaterials after a quantity change, and DisconnectJob returning None. CompatibilityRpcServiceTests.Game_store_backed_calls_read_and_write_the_existing_runtime_tables verifies the service reads materials from the pinned SDE and PostgreSQL inventory and persists/removes monitor records.

Client evidence from the full V24.01 source tree:
- eve/client/script/industry/industrySvc.py:304-326: ConnectJob sends job.dump(), stores the returned monitor ID/material map, and applies matching OnIndustryMaterials updates.
- eve/client/script/ui/shared/industry/industryWnd.py:107: closing the window calls DisconnectJob.
- eve/client/script/industry/job.py:480-488 and industry/material.py:77-90: material consumers read quantities keyed by type ID.
- Current EveJS reference src/server/src/services/industry/industryMonitorService.js:19-31 returns [monitorID, availableMaterials] and null for disconnect.

Verification: Host test project build passed with zero warnings/errors. On eve_p6, the selected Host coverage passed (22 passed, zero failed, zero skipped); the PostgreSQL scan-probe selection passed (35 passed, zero failed, zero skipped). The eve_p6.sde foreign tables point to build 3396210 in eve_p3; test writes ran in eve_p6.

## Completed scan manager rows

Rows RPC-02222–RPC-02227 are covered by the registered Host wire test ScanMgr_full_state_and_registration_include_the_current_signature_map_over_host_wire in dotnet/tests/Eve.Host.Tests/ScanMgrHostWireTests.cs. It obtains the bound scan manager, checks the four-map full-state shape and signature map, then checks SignalTrackerRegister's None reply and full-state/delta notification sequence. The two P2 rows are the devtools caller of the same registered method.

Client evidence from the whole V24.01 source tree:
- eve/client/script/parklife/scanSvc.py:110-115 lazily obtains and caches the bound manager from GetSystemScanMgr. The same file calls RequestScans:192-197, DestroyProbe:264-265, ReconnectToLostProbes:268-278, RecoverProbes:323-340, QAOverrideProbeExpiry:347-353, SetActivityState:425 and :433, and QAScanSites:782-783.
- eve/client/script/parklife/sensorSuiteService.py:164-178 calls SignalTrackerRegister on entering space; :501-528 unpacks the four full-state maps and :531 onward applies deltas; :717 and :721 make direct GetFullState calls.
- eve/client/script/parklife/directionalScanSvc.py:46 calls ConeScan. eve/client/script/parklife/sensorSuiteService.py:531 onward consumes the registration notifications. eve/devtools/script/svc_slash.py:1560-1561 calls SignalTrackerRegister too. eve/client/script/explorationscanner/signalTracker.py:203, :223 and :251 calls GetScanTargetID.
- Current EveJS reference src/server/src/services/exploration/scanMgrService.js:2334-2368 implements registration and full-state notifications.

Verification: on eve_p6, Host coverage passed (22 passed, zero failed, zero skipped); scan-probe tests passed (35 passed, zero failed, zero skipped). The Host wire regression covers GetSystemScanMgr and SignalTrackerRegister. No graphical-client session was used.

The existing scan-manager deep check records that static-site and structure maps in GetFullState have no authoritative Host source yet. The target CSV rows cover the implemented route and signature state behavior; authoritative population of those two maps remains a broader scan-manager gap.
