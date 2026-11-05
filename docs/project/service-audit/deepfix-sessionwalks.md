# Deep session walks: logout notification lifecycle

## Finding and fix

`SafeLogoff` returned an empty failure list and closed the server session after 30 seconds, but did not send the notifications that drive the client's visible countdown and character-selection transition. The V24.01 client calls `shipAccess.SafeLogoff()` from `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:222-228`; `eve/client/script/ui/services/menusvc.py:3208-3211` forwards the menu action. `eve/client/script/parklife/gameui.py:942-968` creates the timer on `OnSafeLogoffTimerStarted` and performs the character-logoff session change on `OnSafeLogoffActivated`. Lines 969-979 close the timer and display the supplied reason or failed-condition labels on abort/failure. Returning success without the two lifecycle notifications therefore left the client connected to a countdown it could not see, then disconnected it without the normal transition.

EveJS 261028.1 sends the timer deadline after the start response and rechecks blockers (`server/src/services/ship/safeLogoffRuntime.js:133-169`), then sends activation before disconnecting (`:171-191`). EVE University documents the 30-second visible safe-logoff timer and combat-timer restrictions ([Timers](https://wiki.eveuniversity.org/GCC)). eve.rs now schedules `OnSafeLogoffTimerStarted` after the RPC response with a FILETIME deadline, sends `OnSafeLogoffActivated` at expiry, then closes the session (`dotnet/src/Eve.Services.Space/SpaceSessions.SafeLogoff.cs:60-73`). The deadline uses wall-clock FILETIME because the Rust space scenes currently advertise 1:1 time (`crates/eve-space/src/scene/session.rs:14, 350, 441`); the code records the limit for future variable time dilation. Movement, target locks, module activation, and player aggression cancel the countdown with the client-consumed abort/failure notification (`SpaceSessions.Modules.cs:44, 84, 123`; `SpaceComposition.cs:438-444`). Session-close cleanup removes the timer without sending to a closed session (`SpaceSessions.cs:1216`).

## Checks

The packet dispatcher sends the RPC response before running these callbacks (`dotnet/src/Eve.Services/PacketDispatcher.cs:252-253`). The focused PostgreSQL wire regression is `Safe_logoff_closes_the_space_session_after_its_timer`: it asserts no timer-start notification before response completion, the one-argument `clientID` timer-start notification, the zero-argument activation notification, and the persisted in-space ship state after session close (`dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:190-227`). `A_movement_command_aborts_a_running_safe_logoff` checks the abort label (`:258-292`). `Aborting_safe_logoff_keeps_the_session_and_ship_in_space` checks explicit user abort (`:228-255`).

## Session-walk coverage map

- Undock, movement/warp, docking, and travel blockers: see [deepfix-travel.md](deepfix-travel.md).
- Combat, dogma, and module cycle checks: see [deepfix-combat and dogma and modules.md](deepfix-combat%20and%20dogma%20and%20modules.md).
- Market item exchange and trade transitions: see [deepfix-market and trade and contracts.md](deepfix-market%20and%20trade%20and%20contracts.md).
- Login-to-space entry and the complete graphical-client login/undock/fight/dock/trade/logoff walk are not established by this change. The evidence here is client source plus Host wire tests. Local verification on `eve_p5` passed the movement abort case and all 172 tests in `SpaceServiceTests` and `PlayerDeathTests`, with no skips. No graphical client session was run for this finding.

