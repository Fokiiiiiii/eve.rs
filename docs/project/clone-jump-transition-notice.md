# Clone jump transition completion notice

## Verified client contract

The V24.01 client defines `OnJumpCloneTransitionCompleted` without parameters in three handlers:

- `eve/client/script/environment/godma.py:605-608` forwards the no-argument event to the active dogma state manager.
- `eve/client/script/environment/godma.py:2619-2631` purges cached inventories, drops the dogma location, primes again, rebinds it when docked or in space, and then calls the skills service.
- `eve/client/script/ui/services/skillsvc.py:955-958` refreshes character attributes and scatters `OnBoosterUpdated`.

A search of the full V24.01 client tree found only these handler definitions and their internal calls; the client does not raise this server notification. The adjacent clone cache invalidation handlers in `clonejumpsvc.py:295-310` clear clone data and scatter clone update events, but do not run the dogma and skills transition above.

## Server flow and implementation

eve.rs registers `JumpCloneService` in `dotnet/src/Eve.Services.Core/CoreServices.cs:84`. `CloneJump` settles through the Host supplied store (`JumpCloneService.cs:466-511`). The PostgreSQL implementation `HostJumpCloneStore.JumpAsync` commits the character, implant, clone, location and wallet changes before returning success (`dotnet/src/Eve.Host/Composition/HostJumpCloneStore.cs:130-248`). The service then relocates the session, invalidates origin and target clone caches, and sends the existing implant and learning updates. It now sends `OnJumpCloneTransitionCompleted` with no arguments once at the end of each successful station/structure or ship clone jump. Refusal and relocation failure paths throw before this send.

EveJS v0.12.9 enters through `server/src/services/station/jumpCloneService.js:103-111`. Its `jumpCloneRuntime.js:2389-2412` finalizes the clone activation, emits origin and destination invalidations, synchronizes Dogma and sends `OnServerImplantsChanged`; it does not send `OnJumpCloneTransitionCompleted`. eve.rs follows the higher-priority V24.01 client contract for this missing notice.

## Regression coverage

`dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs` asserts the updated notification sequences for successful clone jumps, including learning-rate rebases and ship-destination jumps. The added PostgreSQL-backed test `Clone_jump_sends_transition_notice_once_after_success_and_not_after_refusal` verifies exactly one no-argument notice after a successful jump and none after a cooldown refusal. `Eve.Services.Core.Tests.JumpCloneServiceTests.Successful_ship_clone_jump_sends_one_empty_transition_notice_after_settlement` covers the ship success branch and its invalidation order without requiring a simulation process. The existing real-simulation Host test `Confirmed_ship_clone_jump_moves_the_capsule_settles_state_and_notifies_after_space_entry` was skipped because its simulation binary/environment was not configured; it is not counted as passing. The graphical V24.01 client was not run.
