# RPC-02618–02711 client verification, batch 11

## Status

All 30 requested audit rows are recorded in CLIENT_VERIFIED.md. Client callsites and downstream consumers were traced across eve-logic; each route was compared with the Host adapter/provider and EveJS 261018.1. Existing Host wire coverage was present for each distinct method. The populated jump-bridge Host test from the interrupted work was retained and strengthened.

## Changes

- Asset-safety Host wire fixture now supplies the nearest-station KeyVal the client dereferences, with itemID/typeID asserted for personal and corporation wraps.
- Populated jump-bridge Host wire test asserts the endpoint IDs, owner IDs, access lists, structure names and alignment fields consumed by pathfinder, map and deployment code. Test IDs exercise 64-bit wire integers.
- Structure settings Host test asserts the service-ID dictionary key as well as its setting value.
- Removed an invalid using directive from ClientRowsMapHostWireTests.cs merged from main; it blocked compilation of Eve.Host.Tests.

## Verification

- Focused Host tests: asset-safety, structure-settings, and populated jump-bridge methods. Final source has all three passing across the aggregate run and the bridge rerun; one initial bridge assertion failure was corrected and the focused bridge test then passed.
- PostgreSQL composition/restart tests and graphical-client behavior were not exercised.
- No database was used.

## After push

Commit the verified diff, fetch and merge origin/main, then push HEAD to main. Dispatch the focused Eve.Host.Tests Actions run for the three touched wire tests, wait for completion, and inspect the push-triggered ci.yml run. Add the commit SHA and Actions run IDs/results to this handoff after each push.


## Batch 28 — RPC-02630–02642

- Verified all seven audit rows against the V24.01 callsites and whole-tree consumers, Host adapter/composition, the existing real-Host wire test, and EveJS 261018.1. All seven replies match: `GetStructurePilot` returns pilot ID or `None`; `TakeControl`, `Deliver`, and `Anchor` return `None`.
- Recorded each row and the client/Host/EveJS evidence in `CLIENT_VERIFIED.md`.
- Extended `StructureRpcHostCoverageTests.Structure_control_delivery_deployment_and_beacon_calls_use_registered_host_routes` to assert `GetStructurePilot`'s `None` reply over the Host packet loop, exercising the client's no-current-pilot branch. Its callback is a wire-contract fixture, so it does not establish persistence or notification ordering.
- Focused Host test passed (1 passed, 0 skipped). Full `Eve.Host.Tests` run: 470 passed, 101 failed, 227 skipped, 798 total. It ran without a database URL as required; database-backed cases failed while obtaining a connection. Graphical-client behavior and delivery notification ordering were not exercised.
- Remaining: fetch and merge `origin/main`, commit only the two service-audit documentation files, push `HEAD:main`, then inspect the Actions runs as required by the task.
