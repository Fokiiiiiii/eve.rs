# Map RPC host coverage handoff (RPC-02032–02057)

## Result

This batch groups actual Host RPC replies with the client-side Python operations that consume them. The calls named `map.Index`, `map.get`, `map.keys`, and `map.iteritems` in the inventory are methods on reply objects in eve-logic; they are not RPCs sent to the server. Registering Host routes for them would be incorrect.

Added `Map_incursion_and_map_read_replies_keep_client_consumed_shapes_through_host` in `dotnet/tests/Eve.Host.Tests/MapCoverageHostTests.cs`. It registers a real `map` service through `HostServer`, using the existing `IncursionAnomalyService` and `MapService` implementations, sends Macho calls through the Host packet dispatcher, and checks populated incursion rows plus the rowset and list shapes consumed by the map UI. The data source is an in-memory incursion fixture; the test does not open a PostgreSQL connection. The read RPCs do not mutate state, persist data, or emit notifications.

## Whole-client callsites and consumers

- **RPC-02032–02036, `map.GetSystemsInIncursions`:** all six direct callers were found: `mapFilterIncursion.py:20`, `mapViewColorHandler.py:955,984`, and `starModeHandler.py:978,1154` (plus the task inventory’s repeated row at 02033). The filter and both color handlers iterate returned rows and consume `locationID`, `sceneType`, and `templateNameID`; for example `mapFilterIncursion.py:18-25` and `mapViewColorHandler.py:953-962`.
- **RPC-02037–02038, `map.GetTriglavianMinorVictorySystems`:** `mapSvc.py:512-513` forwards the RPC result; `mapFilterTriglavianMinorVictories.py:17-19` builds a system-ID set. The whole tree also uses that map service result in `clientPathfinderService.py:252-253` and `stargate/client/jump_checks.py:76-85`.
- **RPC-02039 and 02041, `Index`:** `sovSvc.py:254-265` calls `GetCurrentSovData` and `GetConstellationLPData`, then indexes their returned rowsets by `locationID` or `solarSystemID`; `uisvc.py:244-248` indexes `GetStationInfo` by `stationID` and locally calls dictionary `.get(stationid, None)`.
- **RPC-02042–02050, `get`:** these are three local lookups on the indexed `GetMyExtraMapInfoAgents` rowset: faction, corporation, and agent standing defaults at `mapFilterMyAgents.py:21-37`, `mapViewColorHandler.py:818-838`, and `starModeHandler.py:800-819`. The indexed records are consumed through their `rank` field.
- **RPC-02051–02057, `get`, `iteritems`, and `keys`:** all are local operations on the dictionary returned by `GetBeaconCount`. The map color callers are `mapViewColorHandler.py:666-701`, `mapFilterCynosuralFields.py:21-53`, and `starModeHandler.py:628-656`; they iterate system/count pairs, take keys, and default missing system counts to zero.

These callers and downstream consumers were checked across the indexed `/home/user/eve-logic` tree. The cited files establish the client-side object APIs and wire result fields.

## EveJS and eve.rs comparison

- EveJS v0.12.9 `server/src/services/map/mapService.js:581-585` reads active incursions and builds the same rowset; `GetSystemsInIncursions` system rows are built by the map payload helper. EveJS `mapService.js:553-557` returns the Triglavian system-ID list.
- For local `Index` and dictionary operations, EveJS provides the source objects, not remote methods of those names. Its `GetCurrentSovData` uses current sovereignty state (`mapService.js:690-694`); station records come from `GetStationInfo` (`701-757`); extra-map agent rows are empty (`656-659`).
- eve.rs serves `GetSystemsInIncursions` from `IncursionAnomalyService.GetSystemsInIncursionsAsync` and returns columns `locationID, sceneType, templateNameID`. `MapService.GetCurrentSovData`, `GetConstellationLPData`, `GetMyExtraMapInfo`, `GetMyExtraMapInfoAgents`, and `GetStationInfo` are the actual producers used by the client-side operations. `GetTriglavianMinorVictorySystems` returns the existing 28-system list. The new regression verifies the real Host registration and wire reply for incursions, sovereignty rowset headers, loyalty/extra-map rowset headers, and the Triglavian list.
- One related state gap remains recorded in `docs/project/TODO.md:1078` and `docs/reference/compat/DEVIATIONS.md:1610`: eve.rs `GetBeaconCount` currently returns an empty dictionary, while EveJS counts active fleet beacons by solar system. The local `.get/.keys/.iteritems` operations are present and accept that reply shape, but this test does not establish active-beacon data parity. The TODO’s fleet-runtime provider is outside these false-positive method rows and remains open.

## Checks and limits

- Passed: focused `Eve.Host.Tests` test `Map_incursion_and_map_read_replies_keep_client_consumed_shapes_through_host` (1 passed, 0 skipped). This compiled the touched test project.
- No PostgreSQL database or graphical client was used.
- `GetStationInfo` reads the SDE through the Rowset Read Side; that database-backed query was not exercised in this job.
