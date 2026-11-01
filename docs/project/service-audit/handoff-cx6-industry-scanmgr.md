# Service audit handoff: cx6

## Completed in this increment

The production implementation and Host registration were already active in the starting revision; this increment adds the missing full Host wire regression and corrects the stale MISSING audit rows. Industry monitor rows RPC-01585–RPC-01588 are covered by the registered Host wire test Industry_monitor_connect_disconnect_and_material_notice_cross_the_registered_host_wire in dotnet/tests/Eve.Host.Tests/IndustryMonitorHostWireTests.cs. It asserts the ConnectJob two-item tuple with a type-ID-to-quantity dictionary, OnIndustryMaterials after a quantity change, and DisconnectJob returning None. CompatibilityRpcServiceTests.Game_store_backed_calls_read_and_write_the_existing_runtime_tables verifies the service reads materials from the pinned SDE and PostgreSQL inventory and persists/removes monitor records.

Client evidence from the full V24.01 source tree:
- eve/client/script/industry/industrySvc.py:304-326: ConnectJob sends job.dump(), stores the returned monitor ID/material map, and applies matching OnIndustryMaterials updates.
- eve/client/script/ui/shared/industry/industryWnd.py:107: closing the window calls DisconnectJob.
- eve/client/script/industry/job.py:480-488 and industry/material.py:77-90: material consumers read quantities keyed by type ID.
- Current EveJS reference src/server/src/services/industry/industryMonitorService.js:19-31 returns [monitorID, availableMaterials] and null for disconnect.

Verification: Host test project build passed with zero warnings/errors. On eve_p6, the selected Host coverage passed (22 passed, zero failed, zero skipped); the PostgreSQL scan-probe selection passed (35 passed, zero failed, zero skipped). The eve_p6.sde foreign tables point to build 3396210 in eve_p3; test writes ran in eve_p6.

## Remaining target rows

Scan manager rows RPC-02222–RPC-02227 remain open for the next increment: GetSystemScanMgr and SignalTrackerRegister (the two P2 rows are the devtools caller of the same registration method).

The existing scan-manager deep check also records that static-site and structure maps in GetFullState have no authoritative Host source yet. Those methods are outside the open CSV rows above; retain that limitation in any broader scan-manager status.
