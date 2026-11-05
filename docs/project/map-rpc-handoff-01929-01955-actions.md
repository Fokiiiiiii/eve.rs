# Map RPC wire handoff follow-up

This addendum updates the Actions status for the map wire test in `map-rpc-handoff-01929-01955.md`.

## Actions for code commit

- Commit: `c0429d97ea90` (map coverage commit `a4d1d542` merged with then-current `origin/main`).
- Focused run `40234403872`: success; the selected Host wire test passed (1 passed, 0 failed, 0 skipped).
- Push gate `40234395258`: failure. `python3 tools/dev/ci-status.py failures c0429d97` reported five failures:
  - `InsuranceRpcHostWireTests.Insurance_rpc_purchase_lookup_list_and_cancellation_follow_the_persisted_host_state`, line 140: expected one matching contract row, received none.
  - `Character_manager_mutations_persist_through_registered_host_wire`, line 62: wire-decoded value is `PyBytes`, assertion expected `PyStr`.
  - `Beyonce_diagnostics_and_formations_use_registered_host_routes`, line 66: wire-decoded value is `PyBytes`, assertion expected `PyStr`.
  - `Ship_fitting_info_and_dirt_timestamp_use_the_registered_host_routes`, line 33: the tuple contents compare as array versus list.
  - `PlayerDeathTests.A_pod_shot_dead_by_another_pilot_carries_destroyed_implants_in_its_loss_record`, line 508: final notification was `OnItemChange`, assertion expected `OnJumpCloneCacheInvalidated`.

The first four failures are also present in the push-gate run `40233984041` for prior main commit `59ae5d50e35c`, before the map coverage commit was pushed. The PlayerDeath failure is outside the map RPC files and appeared in the later run. The map wire test passed in the focused run and does not appear among the push-gate failures.

The graphical client and database-backed production map read models were not part of these checks. The broad CI gate remains red for the failures listed above.
