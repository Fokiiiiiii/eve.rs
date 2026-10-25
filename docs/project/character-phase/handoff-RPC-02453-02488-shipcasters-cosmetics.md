# Host coverage batch: RPC-02453–RPC-02488

## Status

Partial. Commit `a03adf89` (pushed in `3bfe6605c1f8340c5363ea28dc501e4a10aba0db)) adds a loopback Host wire regression for the three shipcaster read methods. The requested end-to-end batch is not complete: production Host composition does not register a shipcaster service or supply authoritative landing-pad/faction state, and the cosmetics, kill-counter and stance paths still need Host wire regressions that exercise their real state owners.

## V24.01 client trace

- Shipcaster: `shipcasterSvc.py:22-29` forwards the three reads. Warzone UI consumes faction IDs at `warzoneMapPanel.py:397`, all-pad results at `:424`, filtered pads and factions at `shipCasterVisualization.py:57,60`, and filtered pads at `warzoneInfoSideCont.py:228`. Consumers inspect faction, system, built, linked and disruption state.
- Apply skin: `shipUtil.py:27` applies the active ship's skin; `cosmetics/client/cosmeticsSvc.py:160-162` applies a skin during the cosmetics flow. GM operations call GiveSkin with duration (`devMenuFunctions.py:8-15`), permanent (`:18-21`), or single-use (`:24-27`) options, expire at `:32-35`, and remove a character or corporation license at `:77-83`. The skin panel exposes GiveSkin and RemoveSkin at `skinPanel.py:633,651`.
- Kill counter: all three client callers are `menusvc.py:869-870`, `baseHangarBehaviours.py:94-97`, and `panelOverview.py:273-285`. The hangar model caps display at 999; ship overview omits a killmark for a zero count.
- Ship stance: inflight selection calls the bound service at `shipstance.py:31`. The fitting center updates stance controls on item/attribute changes at `fittingCenter.py:43,66,252-258`.

## EveJS comparison

EveJS shipcaster reads serialize the in-memory list and faction set in `server/src/services/shipcaster/shipcasterRuntime.js:155-188`; those collections are populated only through test-seed helpers, with no production producer. Cosmetic RPCs delegate to stateful license/ship-skin logic in `server/src/services/ship/shipCosmeticsMgrService.js:308-389` and `shipCosmeticsState.js:327-384,440-460,484-556`. The kill-counter RPC returns the persisted player count as a numeric result (`shipKillCounterService.js:19-31`). SetShipStance calls the runtime and returns the previous stance ID (`shipStanceMgrService.js:35-49`); the runtime updates ship state and broadcasts refreshes (`shipStanceRuntime.js:424-572`).

## eve.rs and verification

The shipcaster RPC service and object-ex wire shape exist in `dotnet/src/Eve.Services.Space/ShipcasterRpcService.cs`; Host production composition does not register it, and `docs/project/TODO.md` records the missing state/faction/eligibility/travel providers. Existing tests cover service-level row formatting. `dotnet/tests/Eve.Host.Tests/ShipcasterHostWireTests.cs` sends all three populated read calls through a loopback Host dispatcher and checks the client class token, six field values and faction list.

Local focused test `Shipcaster_reads_return_client_wire_rows_through_host_dispatch`: 1 passed, 0 failed, 0 skipped. No local database or graphical-client verification was used.

Selected Actions run `40230103524` failed during the Release build before test execution because the merged `ShipRpcHostTests.cs` lacked `using Eve.Services;` for `Py`. The import is now added; the same local focused Host test rebuilt the project and passed. A retry Actions run and push-triggered `ci.yml` status for the corrective commit remain to be recorded. No client graphical run was performed.

Remaining work is production Host registration backed by authoritative shipcaster state, plus real Host wire regressions for cosmetics mutations, kill-counter persistence and stance mutation/notifications.
