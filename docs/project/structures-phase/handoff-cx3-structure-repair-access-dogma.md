# cx3 handoff: Structure repair access and Dogma state

## Status

Implementation commit `7a25d0fe` is on `main` via merge commit `b395ea3a`. Post-merge builds and focused PostgreSQL tests passed. Focused Agent test run `41853409850` succeeded. Push CI run 1507 (`41853285092`) failed in `test_a_moon_chunk_is_refused_until_ready_then_fractured_and_completed_and_its_owners_are_told` when `structureControl.TakeControl` timed out. I reproduced that exact live test locally on `eve_p3`; it passed. Subsequent main CI run 1508 on `c9b26d4891cd` passed, including end-to-end. A selected `test_structures.py` Actions rerun on `b395ea3a` is queued as run 1314. No graphical-client repair walkthrough has been performed.

## Behavior and decisions

- Host wires `RepairService` to the persistent structure directory and checks setting 19 (`SETTING_HOUSING_CAN_DOCK`) through `CharacterHasSettingAsync` on every bound quote and repair RPC. The current profile and access-group rows are read again, so revocation after binding returns `StructureGenericSettingDenied` before a quote or repair mutation. See `dotnet/src/Eve.Host/Rpc/StationRpc.cs:45-46`, `dotnet/src/Eve.Services.Station/RepairService.cs:209-224`, and `dotnet/src/Eve.Host/Persistence/Structures/PersistenceStructureDirectoryReadModel.Part2.cs:525-540`.
- NPC station behavior and existing RPC result shapes are retained. Structure repairs remain free. A core and live structure are required. No service-module-online check is added: Eve University’s [Structure Browser](https://wiki.eveuniversity.org/Structure_Browser) documents repair access with the required core; current EveJS `repairRuntime.js:543-601` rechecks service access, while `structurePayloads.js:211-253` also checks service state. The latter condition is intentionally not copied.
- After commit, changed SDE `damage`, `armorDamage`, and `shieldCharge` values are sent in one `OnModuleAttributeChanges` notification, using the shared `ShipNotifications.AttributeChange` wire row (item ID, attribute ID, FILETIME, new value, old value, FILETIME); existing `OnItemChange` notifications follow it. For NPC repairs, any wallet notification remains first. See `dotnet/src/Eve.Services.Station/RepairService.cs:145-200` and `dotnet/src/Eve.Services.Ships/ShipNotifications.cs:8-13`.
- Client evidence: `eve-logic/eve/client/script/ui/eveCommands.py:2501-2509` gates repair availability; `eve-logic/eve/client/script/ui/station/repairshop/base_repairshop.py:46-72` is the only exact RPC caller found in the full client source and binds with a dock-session check. It uses quote fields at `base_repairshop.py:238-251` and refreshes after repair at `base_repairshop.py:331-337`. `eve-logic/eve/client/script/dogma/clientDogmaLocation.py:309-336` parses the eight-value attribute-change row, updates loaded damage attributes, and raises `OnDamageStateChanged`; its `OnItemChange` path at `569-623` does not update those attributes. The UI consumes the resulting event at `eve-logic/eve/client/script/ui/view/hangarView.py:800-803`; Godma forwards the notification at `eve-logic/eve/client/script/environment/godma.py:112-113,364-365`.

## Checks completed

- `dotnet build dotnet/tests/Eve.Services.Station.Tests/Eve.Services.Station.Tests.csproj -p:UseSharedCompilation=false`: passed, zero warnings/errors.
- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false`: passed, zero warnings/errors.
- On database `eve_p3`, `RepairServiceTests`: 8 passed, 0 skipped. Includes no notifications on insufficient funds and unchanged persisted damage.
- On database `eve_p3`, Host focused filter for `RepairStructureHostWireTests`, `Repair_service_binds_quotes_and_commits_station_repairs_over_host_wire`, and `Structure_settings_host_read_model_uses_persisted_owner_profile_and_access_group_rows`: 3 passed, 0 skipped. Covers revoked access on already-bound calls, no service-online requirement, Dogma item/attribute/timestamp/old/new values, event ordering, persistence, and the NPC wire reply.
- `git diff --check`: passed.
- No Rust files changed.

## After push

Before pushing, check the newest completed `CI` run on main with `python3 tools/dev/ci-status.py status` and follow the red-gate rule. After push, dispatch the focused Actions test job, wait for completion, inspect the push-triggered CI result, and update this handoff with the commit and Actions outcome.
