# alert service

## Client call flow

- The V24.01 alert service sends BeanDelivery and GroupBeanDelivery through alert service proxy aliases. /home/user/eve-logic/carbon/common/script/sys/alert.py:322-416 shows the delivery calls; callers ignore their return values.
- UpdateLogModes calls GetLogModeForError(self.stacktraceLogMode) and consumes (changes, flooded) at /home/user/eve-logic/carbon/common/script/sys/alert.py:796-822. Entries are (errorID, logMode) at :509-520; valid modes are 'B' and 'D' at :22-23.
- Group delivery sends zlib-compressed marshaled group data at /home/user/eve-logic/carbon/common/script/sys/alert.py:298-318.

## Implementation

AlertService is registered by the existing Core RPC composition. BeanDelivery and GroupBeanDelivery validate bounded shapes and log aggregate diagnostic metadata; the grouped path accepts the client’s compressed payload, bounds both compressed and inflated sizes, and sums group counts without retaining member IDs or raw payloads. GetLogModeForError returns the client’s (empty dict, empty list) shape and accepts normal B/D modes plus the client’s None pending pair. The service does not make gameplay state changes or create durable account data.

EveJS 261028.1 records a bounded audit queue at src/server/src/services/alert/alertService.js:188-199; delivery summary behavior and empty log-mode response are at :271-315. The Host keeps logs to metadata counts and does not retain account identity or raw trace/group payloads.

## Verification

- dotnet/tests/Eve.Host.Tests/AlertHostWireTests.cs:Alert_delivery_and_log_mode_rpcs_cross_host_wire_with_client_shapes and :Alert_group_bean_delivery_rejects_non_payloads: PostgreSQL eve_p4, 2 passed, 0 failed, 0 skipped.
- Alert-related Host tests: 6 passed, 0 failed, 0 skipped; Core alert tests: 3 passed, 0 failed, 0 skipped.
- A graphical V24.01 client session was not run.
