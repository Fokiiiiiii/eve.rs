# Provenance of `eve-behaviors`

A port, as it is, of the `behaviors` package of the EVE client V24.01 (decompiled; `Fokiiiiiii/eve-logic`, directory
`behaviors/`). See `docs/design/adr/0008-behaviors-package-is-ported-as-is.md` for why and under what rule.

| Rust | Client module |
|---|---|
| `src/value.rs` | the values Python passes around (no module of its own) |
| `src/blackboards.rs` | `blackboards/__init__.py`, `blackboards/scopes.py`, `blackboards/utility/*` |
| `src/events.rs` | `EventMessenger` of the server's ballpark, as the tasks use it (`SubscribeItem`, message ids) |
| `src/timers.rs` | `timers/__init__.py` |
| `src/tree.rs` | `behaviortree.py`, `tasks.py`, `status.py`, `exceptions.py` |
| `src/composites.rs` | `composites/__init__.py`, `composites/weighted.py` |
| `src/decorators.rs` | `decorators/__init__.py`, `decorators/modifiers.py`, `decorators/repeaters.py`, `decorators/timers.py` |
| `src/evemath.rs` | `evemath/__init__.py` (the functions the tasks use) |
| `src/env.rs` | the server-side calls the tasks make (`ballpark`, `dogmaLM`, ...): the `World` trait, no client module |
| `src/consts.rs` | the constants the tasks read of `dogma.const`, `inventorycommon.const`, `eve.common.lib.appConst`, `carbon.common.lib.const`, and the ids of `idCheckers` |
| `src/constants/*` | `const/behaviorroles.py`, `const/blackboardchannels.py`, `const/combat.py`, `const/loot.py` |
| `src/utility/*` | `utility/*` (`awareness`, `ballparks`, `battlefield`, `blackboards`, `components`, `context`, `dogmatic`, `dungeons`, `entity_groups`, `groups`, `inventory`, `item_modules`, `logistics`, `math`, `owner`, `roles`, `security`, `standings`, `structure`, `threat`); `spawning` follows with the actions that use it |
| `src/groups/*` | `groups/__init__.py`, `groups/mixin.py` |
| `src/conditions/*` | `conditions/*` (all seventeen files) |
| `src/monitors/*` | `monitors/*` (all nineteen files); `monitors/mod.rs` holds `MonitorTask` of `tasks.py` |
| `src/macros.rs` | (none: `enter_task!` makes a class with an `OnEnter`) |
| `src/actions/*` | `actions/__init__.py`, `awareness`, `ballparks` (`ballparks/{movement,selection,locations,slim}.rs`), `battlefield`, `blackboards`, `combat`, `combatmodules`, `combatnavigation`, `combattargets`, `damage`, `drones`, `dungeon`, `effects`, `entities`, `entityspawning`, `eventlogger`, `fighterabilities`, `fleet`, `group`, `guard`, `hauling`, `logistics`, `map`, `mathtasks`, `objectspawning`, `operations`, `randomized`, `reinforcements`, `resourcewars`, `rewards`, `structures`, `tale`, `tanking`, `timer` and `reference_subtree` (the rest follows) |
| `src/env/spawning.rs` | the server's spawning machinery that `entityspawning` and `utility/spawning` call (`spawndataforentities`, `npcspawning`, `spawnlists`, the managers of `entityLocation`): the `Spawning` trait, no client module |
| `src/utility/spawning.rs` | `utility/spawning.py` |
| `src/target_evaluation.rs` | `targetEvaluation/*` |
| `src/registry/*` | `registry/*` (`__init__`, `registrator`, `taskregistry`, and the five tables `actions`, `conditions`, `composites`, `decorators`, `monitors`, whose entries are generated one to one from the client's `register_*` calls: class name, kind, descriptions, attributes with their index, default and optionality); a class is made by a function instead of being imported by its name; the attribute kinds are `fsd/attributes.py` |
| `src/loaders/*` | `loaders/*` (`fsd_behavior_loader.py` over a `BehaviorTreeStorage` trait, `scripted_behavior_loader.py`); `storage.py` is that trait: the client's `fsdlite` storage reads `behaviortrees.static`, which is not in the extract |
| `src/entity.rs` | `entity.py` (`BehaviorEntity`: the additional loot, `NPCWarpTo`/`NPCEndWarp`, `OnExplode`, `OnUnspawn`, `DoModeChange`; the server's `EntityWrapper`, ballpark, inventory and loot service it leans on are the `EntityHost` trait) |
| `src/storage.rs` | `storage.py` (the data class `BehaviorTree`; the storage itself is the `BehaviorTreeStorage` trait of `loaders/fsd.rs`) |
| `src/debugging.rs` | `debugging/behaviortreemonitor.py` (the monitor, `DumpTree`, `GetBlackboards`); `debugging/tools.py` is not in the extract, its `DepthFirstTaskIterator` is `depth_first_tasks` |
| `src/trees/*` | `trees/*` (all forty-six files: the scripted trees, built by `trees::builder::Node` out of registry class names) |

Server-side pieces that a tree names and the client's files do not have (`npcs.server.tags.DRIFTER_TAG`) are constants of `trees/mod.rs` with the name they are
agreed under.

Also read for facts, not ported: `npcs/common/standings.py` (`classify_standings`), `eveuniverse/security.py` (`SecurityClassFromLevel`),
`dogma/attributes/health.py` (the ratios), `crimewatch/util.py` (`GetAggressorId`), `evemath/__init__.py` (`env`/`evemath`).

