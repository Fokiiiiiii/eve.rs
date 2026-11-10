# EveJS 261108.1: clone, MCT, and ScoopToSMA audit

This audit compares `/home/user/evejs-261108.1` with `/home/user/evejs-261028.1`, the V24.01 client source in `/home/user/eve-logic`, and the pinned SDE `eve-online-static-data-3396210-jsonl`. Local file references below are paths with line numbers.

## Client behavior checked

- Clone UI reads `GetCloneState` and caches both clones, clone implants, and `timeLastJump` (`eve/client/script/ui/services/clonejumpsvc.py:73-80`); clone implants are selected by `jumpCloneID` (`:272-277`). A clone jump goes through `CloneJump` and the client session-change flow (`:193-210`). `OnServerImplantsChanged` refreshes attributes and emits the implant and skill-queue refresh events (`eve/client/script/ui/services/skillsvc.py:942-948`).
- The SDE dogma attribute `followsJumpClones` is ID 1916 and says it stays with the character across clone changes, only for implants and boosters (`.cache/sde/eve-online-static-data-3396210-jsonl/dogmaAttributes.jsonl:1650`).
- The client handles `OnMultipleCharactersTrainingUpdated` by refreshing its cache and slot-use calculation (`eve/client/script/ui/services/skillQueueSvc.py:841-851`). Its active-slot check counts only *other* characters (`:859-871`). The MCT window displays up to two extra slots from `GetUpdatedMultiCharacterTraining` and sends a selected `trainingID` when activating or extending (`eve/client/script/ui/shared/activateMultiTraining.py:138-171,221-231`).
- The client offers ScoopToSMA only for another ship, not a capsule, when the active ship has an SMA (`/home/user/eve-logic/menucheckers/celestialCheckers.py:1302-1311`). It approaches to interaction range and calls `ship.ScoopToSMA(objectID)` (`eve/client/script/ui/services/menusvc.py:3323-3332`). The offer condition does not require that the target ship belong to the current character.

## Ported

### Jump-clone follower implants

Eve.rs did not preserve SDE-marked follower implants when changing clone bodies. The Host now reads attribute 1916 from the SDE, keeps marked implant item rows on the pilot, and lets those implants override the target clone implant in the same slot. The transaction rebases training before changing learning attributes and sends the client implant refresh after the committed body swap (`dotnet/src/Eve.Host/Composition/HostJumpCloneStore.cs:153-161,211-222,250-296`; `dotnet/src/Eve.Services.Core/JumpCloneService.cs:513-518`). The bound Host regressions check stored clone/body implant rows and notification order, plus a running memory-primary queue whose end time lengthens when the new body loses its memory bonus (`dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:453-495,498-546`).

The new and prior EveJS versions already classify and merge followers by slot (`/home/user/evejs-261108.1/server/src/services/station/jumpCloneRuntime.js:133-136,190-245,1880-1908`; prior version has the same behavior at `:1886-1913`). The implementation follows that behavior while using the official SDE flag and the existing PostgreSQL item owner.

### MCT expiry boundary and queue notifications

Eve.rs already had the client RPC shapes, certificate consumption, persisted slots, and extra queue entitlement (`dotnet/src/Eve.Services.Core/UserSvcCompatibility.cs:12-25,28-49`; `dotnet/src/Eve.Services.Core/MultipleTrainingCallbacks.cs:9-28`). It did not settle a delayed or missed expiry at its saved boundary. MCT jobs now carry the slot's FILETIME; startup rebuilds future jobs and processes missed expiries chronologically. At each exact boundary it measures queue SP, pauses the lowest-SP queue, and preserves later training only for characters whose queues remain active (`dotnet/src/Eve.Services.Skills/MultipleTrainingSlotExpiry.cs:20-69,79-118`; historical views and pauses are in `dotnet/src/Eve.Skills/CharacterTraining.cs`). Tests cover a late scheduled expiry, a host restart across expiry, and a stale job after extension (`dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:3172-3241,3244-3295,3298-3363`).

On a successful `SaveNewQueue`, the Host now sends the queue and optional paused state without `OnMultipleCharactersTrainingUpdated` (`dotnet/src/Eve.Services.Skills/TrainingAnnouncer.cs:162-177`). EveJS 261108.1 makes the same distinction (`/home/user/evejs-261108.1/server/src/services/skills/training/skillQueueRuntime.js:1572-1577`). The client uses that account-wide event to invalidate the MCT cache, while its slot-use calculation excludes the character whose queue was just saved. Host coverage verifies the exact saved-queue wire notification; stop/expiry still sends the account update (`dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:3365 onward`; `dotnet/src/Eve.Services.Skills/TrainingAnnouncer.cs:226-239`).

CCP documents one extra simultaneous queue per active 30-day MCT service and up to two extra queues ([Multiple Character Training support](https://support.eveonline.com/hc/en-us/articles/203411052-Multiple-Character-Training)). CCP's Dual Character Training patch note says the character with the least skill points stops when that service expires ([Odyssey patch notes](https://www.eveonline.com/ru/news/view/patch-notes-for)). EveJS 261108.1 also settles the lowest-SP queue at expiry, using the expiry time for delayed settlement (`/home/user/evejs-261108.1/server/src/services/skills/training/skillQueueRuntime.js:340-404,737-742,1580-1588`).

### ScoopToSMA

This RPC was not wired in eve.rs. It is now registered on the bound `ship` service, checks the active ship's SDE-backed bay, a same-system abandoned non-capsule ship, and the 2,500 m interaction range; it asks the simulation to remove the ship before committing the inventory transfer. A refusal leaves inventory unchanged and can be retried (`dotnet/src/Eve.Services.Space/ShipService.cs:271-279`; `dotnet/src/Eve.Services.Space/SpaceSessions.ScoopToSma.cs:11-84`; `dotnet/src/Eve.Ships/CharacterShips.ScoopToSma.cs:8-53`). Real Host tests verify transfer to the bay, the packed `OnItemChange` row, simulation removal, refusal rollback/retry, and out-of-range refusal (`dotnet/tests/Eve.Services.Space.Tests/ScoopToSmaHostTests.cs:8-88`).

The client action and eligibility checks above establish the call signature and user-visible path. EveJS implements the same interaction and distance checks (`/home/user/evejs-261108.1/server/src/services/ship/ejectRuntime.js:1324-1396`). The implementation uses the existing ship-bay capacity and contents rules instead of introducing a second inventory store.

## Already present; no port needed

- Clone cooldown already uses Infomorph Synchronizing (type 33399), one hour per level from 24 hours, and persists only remote jump times (`dotnet/src/Eve.Services.Core/JumpCloneService.cs:426-434`; `dotnet/src/Eve.Characters/JumpClones.cs:149-216`; `dotnet/src/Eve.Services.Core/JumpCloneService.cs:46-60`). Existing Host tests cover same-location switching without resetting the cooldown, a remote jump, refusal during cooldown, restart persistence, zero-cost reset, and concurrent attempts (`dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:359 onward,387-451,498-541`). EveJS 261108.1 fixes its old same-structure local-swap cooldown update (`jumpCloneRuntime.js` diff around `:1857-1860,1913-1916`); eve.rs already has the intended result.
- MCT slot numbering (2/3), 30-day certificate grants, opening a free slot before extending the earliest-ending slot, and active-slot wire formats were already present. No protocol or entitlement redesign was taken from EveJS.

## Rejected or not ported

- EveJS 261108.1 adds a synthetic active MCT slot while the AIR tutorial is active (`/home/user/evejs-261108.1/server/src/services/user/userService.js:194-219`). No matching CCP entitlement or V24 client request was found; the client simply uses the server's returned slot count. Since the official service is a paid/consumed 30-day entitlement, this synthetic grant is not ported without TQ evidence.
- EveJS `scoopShipIntoActiveShipMaintenanceBay` rejects a target whose owner differs from the current character (`/home/user/evejs-261108.1/server/src/services/ship/ejectRuntime.js:1354-1356`). The V24 client offer predicate has no owner check (`/home/user/eve-logic/menucheckers/celestialCheckers.py:1302-1311`), and the target is explicitly an abandoned ship. The Host therefore permits taking another character's abandoned ship and records the new owner on successful transfer. The test uses a ship ejected by a different pilot (`ScoopToSmaHostTests.cs:90-121`).
- EveJS's process-global timer/caches and lazy settlement are not copied. eve.rs uses the existing durable scheduler, PostgreSQL slot rows, `CharacterTraining`, and `CharacterSessions` to implement the same client-visible expiry outcome.

## Verification boundary

The checks are headless Host/service/database tests against `eve_p4` and the pinned V24.01 SDE. They verify registration, RPC arguments/results, packed notifications, persistent state, scheduler restart and refusal paths. No graphical V24.01 client session or live Tranquility comparison was run.
