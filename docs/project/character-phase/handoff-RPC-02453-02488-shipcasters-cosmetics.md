# Host coverage batch: RPC-02453–RPC-02488

## Status

Partial. Added a loopback Host wire regression for the three shipcaster read methods. The requested end-to-end batch is not complete: the production Host does not register a shipcaster service or supply authoritative landing-pad/faction state, and the batch's cosmetics, kill-counter and stance paths still need Host wire regressions that exercise their real state owners.

## V24.01 client trace

- Shipcaster: `shipcasterSvc.py:22-29` forwards the three reads. Warzone UI consumes faction IDs at `warzoneMapPanel.py:397`, all-pad results at `warzoneMapPanel.py:424`, filtered pads and factions at `shipCasterVisualization.py:57,60`, and filtered pads at `warzoneInfoSideCont.py:228`. The consumers inspect pad faction, system, built, linked and disruption state.
- Apply skin: `shipUtil.py:27` applies the active ship's skin; `cosmetics/client/cosmeticsSvc.py:160-162` applies a skin during the client cosmetics flow. GM operations call GiveSkin with duration (`devMenuFunctions.py:8-15`), permanent (`:18-21`), or single-use (`:24-27`) options, expire at `:32-35`, and remove a character or corporation license at `:77-83`. The skin panel exposes GiveSkin and RemoveSkin at `skinPanel.py:633,651`.
- Kill counter: all three client callers are `menusvc.py:869-870`, `baseHangarBehaviours.py:94-97`, and `panelOverview.py:273-285`. The hangar model caps display at 999; the ship overview omits a killmark when the returned count is false.
- Ship stance: inflight stance selection calls the bound service at `shipstance.py:31`. The fitting center also updates stance controls on item/attribute changes at `fittingCenter.py:43,66,252-258`.

## EveJS comparison

EveJS shipcaster reads serialize the in-memory list and faction set in `server/src/services/shipcaster/shipcasterRuntime.js:155-188`; those collections are populated only through test-seed helpers, with no production producer. Cosmetic RPCs delegate to stateful license/ship-skin logic in `server/src/services/ship/shipCosmeticsMgrService.js:308-389` and `shipCosmeticsState.js:327-384,440-460,484-556`. The kill-counter RPC returns the persisted player count as a numeric result (`shipKillCounterService.js:19-31`). SetShipStance calls the runtime and returns the previous stance ID (`shipStanceMgrService.js:35-49`); the runtime updates ship state and broadcasts refreshes (`shipStanceRuntime.js:424-572`).

## eve.rs and verification

The shipcaster RPC service and object-ex wire shape exist in `dotnet/src/Eve.Services.Space/ShipcasterRpcService.cs`; Host production composition does not register it, and `docs/project/TODO.md` records the missing state/faction/eligibility/travel providers. Existing unit tests cover service-level row formatting; the added `dotnet/tests/Eve.Host.Tests/ShipcasterHostWireTests.cs` sends all three populated read calls through a real loopback Host dispatcher and checks the client class token, six field values and faction list.

Local focused test: `Shipcaster_reads_return_client_wire_rows_through_host_dispatch` passed (1 passed, 0 failed, 0 skipped). No database or graphical-client verification was used. Remaining work is production Host registration backed by authoritative shipcaster state, plus real Host wire regressions for cosmetics mutations, kill-counter persistence and stance mutation/notifications.
