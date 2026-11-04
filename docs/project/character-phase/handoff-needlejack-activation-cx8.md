# Needlejack activation handoff (cx8)

## Scope and behavior

The Host registers a concrete `NeedlejackActivation` callback for `randomJumpMgr.ActivateRandomJumpFilament` in `SpaceRpc`. For group 4041 it locks and revalidates the cargo item, SDE dogma, fleet, sessions, ship positions and safety; selects an SDE-backed nullsec system from the available pending NPC bounty activity proxy; and commits filament consumption, fleet relocation, timer state and the retry record in one PostgreSQL transaction. A repeated call with the same session and CallId completes post-commit notifications without another consumption.

The selection proxy is limited to pending NPC bounty kills currently stored per system. Noise selects the least active score and Signal the highest; ties use ascending system ID. Neither the source nor available state defines a complete activity measure, time window or tie distribution, so no probability is implied.

## Client evidence

- V24.01 `randomJump/client/randomJumpSvc.py:16-19` sends one item ID; `randomJumpController.py:54` wraps the RPC as a `RandomJump` session change.
- `randomJumpController.py:141-167` reads the `RandomJumpError` `errors` list. `clientRandomJumpValidator.py:44-57,107-108,150-196` checks supported groups, ownership, quantity, timer and local activation restrictions.
- `inventorysvc.py:56-100` consumes `OnItemChange` and treats quantity zero as removal. `crimewatchSvc.py:227-232` consumes `OnPvpTimerUpdate(state, expiryTime)`; `randomJumpController.py:192-194,213-220` revalidates after timer and session changes.
- The client also has a separate trace path: `menuSvcExtras/movementFunctions.py:367,515-525` calls `FILAMENT_SPOOLUP.JumpThroughRandomFilamentGate`; `filamentSpoolup.py:24-36` validates before that action. UniWiki documents nearby fleet linking and a spool-up timer for Needlejack travel ([Needlejack filaments](https://wiki.eveuniversity.org/Needlejack_filaments), [Filament](https://wiki.eveuniversity.org/Filament)).
- SDE group 4087 is Pochven filament data, without the Needlejack Noise/Signal attribute 3025. The nullsec Needlejack path handles group 4041 only; Pochven routing is separate. The client validator does list both groups, so group 4087 remains a compatibility gap pending its Pochven route.
- The Host moves pilots on item activation and does not create the separate trace/spool-up gate lifecycle. This matches the requested atomic activation callback scenario, but is not verified against the graphical client’s two-stage travel interaction.
- EveJS 261028.1 `server/src/services/activity/randomJumpMgrService.js:146-164` currently refuses with error 22 because its destination runtime is unavailable.

## Tests run locally

The Host and adjacent Space tests below ran against `eve_p8` through `tools/dev/db-lock.sh` after merging `origin/main` through `0064fd01`.

- Host filter `NeedlejackActivationHostTests|Random_jump_rejection_keeps_the_client_error_envelope_through_host_wire|Movement_rpcs_use_registered_host_routes_with_client_wire_shapes`: 10 passed, 0 skipped. The new `Host_rejects_capital_hulls_and_fleets_above_the_sde_candidate_limit` regression checks the SDE-derived hull limit and client error envelope.
- Space `ActivityTravelRpcServicesTests|RelocationTests|PlayerDeathTests`: 36 passed, 0 skipped.
- The `Eve.Host.Tests` and `Eve.Services.Space.Tests` projects compiled with 0 warnings and 0 errors.
- Before the merge, `PgBountyStoreTests` passed 16/16, Services Crimewatch `RuntimeTests|PlayerBountyKillsTests|NpcSecurityStatusRecoverySubscriberTests` passed 38/38, and Crimewatch `AggressionTests|SafetyTests` passed 45/45. After the merge, the `PgBountyStoreTests --no-build` command could not start because that test assembly was absent; these store tests have not been repeated on the merged tree.
- `cargo +1.94.1 fmt --all -- --check` and workspace `clippy --all-targets -- -D warnings` passed before this test-only change.

The earlier red-gate reproduction for `Movement_rpcs_use_registered_host_routes_with_client_wire_shapes` matched main CI 1484 and was fixed upstream in commit `6e73707b`; the test passed in the Host run above. Main CI 1491 for `ce18bc3f` and 1492 for `d2f3204e` passed. Main CI 1493 for `0064fd01` was in progress at the last check. The focused Agent Test run 41797872638 and push-triggered CI run 41797832335 for `ce18bc3f` passed; `ci-status.py failures ce18bc3f` reported no failed jobs. No graphical EVE client session was run.
