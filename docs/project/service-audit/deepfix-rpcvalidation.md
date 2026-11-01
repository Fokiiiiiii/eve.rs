# RPC Validation Deepfix Handoff

## Current checkpoint: main CI docking regression

The newest completed main CI run at the start of this work was red at `f8396506`: `test_a_moon_chunk_is_refused_until_ready_then_fractured_and_completed_and_its_owners_are_told` failed while `_docked_pilot` received `DockingRequestDenied`. The failure occurs before the moon RPC under test.

The test helper now retries one `DockingRequestDenied` after waiting 11 seconds, then retains the existing 120-second deadline and requires eventual docking. Other docking refusals still fail; a repeated denial fails. This accommodates the server's session-change rule without accepting a denied docking as success.

## Evidence for the decision

- V24.01 `eve/client/script/ui/services/structure/structureDocking.py:64-81`: `StructureDocking.Dock` issues `structureDocking.Dock(structureID, session.shipid)` from the autopilot callback; it does not locally wait for the session timer. Its `DockingRequestDenied` handler scatters `OnDockingRequestWasDenied`, except for the ship-too-large reason.
- V24.01 `eve/client/script/ui/services/structure/structureDirectory.py:133-139`: the denial event refreshes the docking list only when the structure is still dockable.
- eve.rs `dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:37-41,287-303`: `DockAsync` applies the shared movement policy; it includes `TransitionRuntime.SessionChangeActive`.
- Current EveJS `server/src/services/structure/structureDockingService.js:137-182`: its handler checks structure/access, module restrictions, approach distance and docking acceptance, but not session-change cooldown.
- EVE University [Timers](https://wiki.eveuniversity.org/GCC): the 10-second session-change timer prevents docking and other session changes after docking/undocking, login and gate travel. The server guard follows this gameplay source; the headless setup now allows that timer to expire before asserting successful docking.

## Local check

Passed the focused live headless test against the Host, network front and simulation with PostgreSQL database `eve_p3`, through `tools/dev/db-lock.sh`. Host and simulation used the loaded SDE build 3405148. System pytest emitted warnings that the optional timeout config keys are unavailable in this environment.

## Remaining task scope

The requested audit across Host RPC input-validation paths and core player flows has not been completed. Continue with Host-bound RPC groups, trace all V24.01 callers and consumers, compare the current EveJS and authoritative Host/sim state, add root-cause regression tests, and append evidence and results here. Do not treat this checkpoint as completion of the audit.
