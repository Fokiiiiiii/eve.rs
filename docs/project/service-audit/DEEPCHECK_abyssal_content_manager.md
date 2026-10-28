# Deep check: abyssal_content_manager

Source snapshot: main commit `7a3a02e9`; client: V24.01 eve-logic commit `1dbb5711a072a146e1d228365668ac05655eefe1`.

## Client call and reply flow

| Method | V24.01 caller | Arguments and consumed reply |
|---|---|---|
| `GetContentDetailsForShipLocation` | `eve/devtools/script/abyssal_content_tool.py:110-119` | No arguments. Truthy result is read by `_load_content_ui` at :78-83 for `entry_item_ids_by_location_id` and `plan`; the tool also displays `content_id` at :113-118. |
| `GenerateContentFromExistingPlan` | `abyssal_content_tool.py:140-151` | One positional serialized plan string. Result uses the same content fields and is displayed at :147-151. |
| `GenerateAbyssEncounterContent` | `abyssal_content_tool.py:184-201` | Six positional values: difficulty tier, dungeon ID, weather effect type ID, nebula graphics ID, lens flare type ID, and spawn table ID. Result uses the same content fields. |
| `generate_abyss_npc_spawn_report` | `eve/devtools/script/abyssal_npc_spawn_tool.py:72-89` | Three positional integers: difficulty tier, spawn table ID, repetitions. `format_results` at :90-109 consumes averages and the `spawns` list, including NPC/group names, IDs, costs, quantities, and tags; CSV writers at :111-169 consume the same records. |

All callers are QA/developer tools, not the player UI. The tools catch exceptions and show feedback text; the report tool also clears its output on errors. The client does not request keywords or a macho version.

## Host and EveJS

The Host registers `AbyssalContentManagerService` from `dotnet/src/Eve.Services.Core/CoreServices.cs:62`, but its only method is `generate_abyss_npc_spawn_report` (`UnportedFeatureRpcServices.cs:12-33`). The three content-management methods have no Host route; PacketDispatcher returns a wrapped AttributeError stating that the service has no such method (`dotnet/src/Eve.Services/PacketDispatcher.cs:166-179`). The report callback is optional in `GameRpcServices.cs:29`; production callback construction in `Eve.Host/Composition/CharacterComposition.cs:225-260` supplies no `AbyssalSpawnReportAsync`, so the live Host returns exact `UserError` `ABYSSAL_SPAWN_REPORT_UNAVAILABLE` rather than a report.

EveJS v0.12.9 also has the report handler at `server/src/abyssal/transport/abyssalContentManagerService.js:981-1020`, but when no report provider exists it fabricates an empty report. The three content methods were not found in the EveJS source search. The Host refusal is intentional because this repository forbids fabricated success data; it is a compatibility gap, not a verified equivalent. The Host also rejects nonstaff callers with `ABYSSAL_SPAWN_REPORT_STAFF_ONLY` and malformed/out-of-range inputs with `ABYSSAL_SPAWN_REPORT_ARGUMENTS_INVALID` or `ABYSSAL_SPAWN_REPORT_INPUT_INVALID`.

There is no persistence or notification flow for these devtools RPCs. Existing service tests cover callback forwarding and the unavailable error at `dotnet/tests/Eve.Services.Core.Tests/MiningAndAbyssalRpcTests.cs:22-48,76-112`; no registered Host wire test covers these methods. Implementing the missing content service and its authoritative data provider remains open. No PostgreSQL database or graphical client was used.
