# Service audit handoff: cx6 ship configuration

## Completed

Implemented `ship.GetShipConfiguration` and bound `ship.ConfigureShip` with PostgreSQL-backed per-ship settings, default-false values, partial updates, and production Host migration/wiring. The Host wire regression calls ConfigureShip through MachoBindObject, reads all reply keys, and checks persisted values.

Whole V24.01 client evidence:
- `eve/client/script/ui/services/shipConfigSvc.py:50-57` and `eve/client/script/ui/shared/shipconfig.py:45-46`: both paths read the configuration mapping.
- `shipConfigSvc.py:59-87`: sends one-key dictionaries, updates its local cache, and consumes the four SMB/fleet-hangar booleans. No server notification is consumed.
- Current EveJS reference `/home/user/evejs-261028.1/src/server/src/services/ship/shipService.js:2193-2293` returns the same six-key dict and merges recognized keys.
- Host wire test: `dotnet/tests/Eve.Host.Tests/ShipConfigurationHostWireTests.cs:Ship_configuration_changes_persist_through_the_registered_host_wire`.

The pinned client source also shows planet GetPin rows RPC-02136/02137 are local colony model calls: `BasePinContainer.py:609-610`, `planetUIPins.py:473-474`; the object method delegates through `basePlanet.py:45-53` to `baseColony.py:67-76`. Their coverage rows are marked CLIENT-LOCAL.

## Remaining rows

- `facWarMgr`: RPC-01466 GetStats_CorpPilots and RPC-01476 RefreshCorps. Whole client search only finds the wrapper declarations at `facWarSvc.py:304-305,385-386`; no client consumer was found.
- `ship`: RPC-02278 LaunchFromContainer and RPC-02280 ResetDirtTimestamp. Reset also needs durable dirt state and the slim-item notification described by EveJS.
- `shipCasterLandingPadMgr`: RPC-02281–02284. Client UI consumes faction, linked, built, disrupted, and solar-system state (`shipcasterSvc.py:23-27`, `shipCasterVisualization.py:57-91`, `warzoneInfoSideCont.py:228-239`, `warzoneMapPanel.py:424-430`). A production-authoritative landing-pad state provider is still required; see [EVE University Shipcaster](https://wiki.eveuniversity.org/Shipcaster).

## Verification

On PostgreSQL database eve_p6: Host wire test passed (1/1); ship store test passed (1/1); existing ship Host tests passed (8/8); Service.Space focused ship tests passed (6/6); the full SpaceServiceTests class passed (149/149). All selections had zero skips. No graphical client session was used.
