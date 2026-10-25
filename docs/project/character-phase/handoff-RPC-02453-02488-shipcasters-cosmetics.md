# Host coverage batch: RPC-02453–RPC-02488

## Status

Partial. Commit `a03adf89` (included in pushed SHA `3bfe6605c1f8340c5363ea28dc501e4a10aba0db`) adds a loopback Host wire regression for the three shipcaster read methods. The requested batch is not complete. Production Host registers the shipcaster service names in `AgentRpc.cs:110`, but provides no landing-pad/faction state. Cosmetics and stance still need Host wire regressions; kill-counter now has one.

## V24.01 client trace

- Shipcaster: `shipcasterSvc.py:22-29` forwards the three reads. Warzone UI consumes faction IDs at `warzoneMapPanel.py:397` and `shipCasterVisualization.py:60`, all-pad rows at `warzoneMapPanel.py:424`, and faction-filtered pads at `shipCasterVisualization.py:57` and `warzoneInfoSideCont.py:228`. These consumers use faction, system, built, linked and disruption state.
- Apply skin: `shipUtil.py:27` applies the active ship's skin; `cosmetics/client/cosmeticsSvc.py:160-162` applies a skin during the cosmetics flow. GM operations call GiveSkin with duration (`devMenuFunctions.py:8-15`), permanent (`:18-21`), or single-use (`:24-27`) options, expire at `:32-35`, and remove a character or corporation license at `:77-83`. The skin panel exposes GiveSkin and RemoveSkin at `skinPanel.py:633,651`.
- Kill counter: all three client callers are `menusvc.py:869-870`, `baseHangarBehaviours.py:94-97`, and `panelOverview.py:273-285`. They consume a numeric count for the ship menu, hangar model (capped at 999), and overview killmark (zero suppresses the mark). Whole-tree `search_for_pattern` found no other callers or consumers.
- Ship stance: inflight selection calls the bound service at `shipstance.py:31`. The fitting center updates stance controls on item/attribute changes at `fittingCenter.py:43,66,252-258`.

## EveJS comparison

EveJS shipcaster reads serialize the in-memory list and faction set in `server/src/services/shipcaster/shipcasterRuntime.js:155-188`; those collections are populated only through test-seed helpers, with no production producer. Cosmetic RPCs delegate to stateful license/ship-skin logic in `server/src/services/ship/shipCosmeticsMgrService.js:308-389` and `shipCosmeticsState.js:327-384,440-460,484-556`. The kill-counter RPC returns the persisted player count as a numeric result (`shipKillCounterService.js:19-31`). SetShipStance calls the runtime and returns the previous stance ID (`shipStanceMgrService.js:35-49)); the runtime updates ship state and broadcasts refreshes (`shipStanceRuntime.js:424-572`).

## eve.rs and verification

The shipcaster RPC service and object-ex wire shape exist in `dotnet/src/Eve.Services.Space/ShipcasterRpcService.cs`. Host production registers them in `dotnet/src/Eve.Host/Rpc/AgentRpc.cs:110`, but passes no landing-pad, faction, eligibility or travel callbacks; `docs/project/TODO.md` records those gaps. `ShipcasterHostWireTests.cs` verifies populated rows through Host dispatch, including the client class token, six fields and faction list.

`ShipKillCounterHostWireTests.Ship_kill_counter_returns_the_persisted_player_count_through_host_dispatch` sends `GetItemKillCountPlayer(9001)` through the real loopback Host and asserts the persisted `playerKills` count returns as `PyInt(23)`. Local `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj` succeeded with one transient file-copy retry warning caused by overlapping MSBuild processes; focused test: 1 passed, 0 failed, 0 skipped. No database or graphical-client verification was used.

The first selected Actions run `40230103524` failed at Release compilation before the test because merged `ShipRpcHostTests.cs` lacked `using Eve.Services;` for `Py`. The missing namespace was added in `916e0bca` and pushed in `d8ca628437402c29b442bdc033313ca9bd836680`. Retry `40231225904` passed the Release build and selected test; no tests or jobs failed. The push-triggered `ci.yml` status lookup for that SHA returned no run. No graphical client was used.

Remaining work is an authoritative shipcaster state provider and Host wire regressions for skin mutations and stance mutation/notifications.
