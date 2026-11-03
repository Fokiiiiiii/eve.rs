# Capsuleer PvP timer handoff (cx8)

## Behavior and source evidence

- EVE University [Timers](https://wiki.eveuniversity.org/GCC) says a 15-minute capsuleer log-off timer starts when a pilot shoots or is shot by another capsuleer, blocks filament use, and does not give a pilot a weapons timer merely for being shot.
- V24.01 initializes `pvpTimerState` and expiry to None in `eve/client/script/ui/services/crimewatchSvc.py:68-69`; session hydration reads the five pairs from `GetClientStates` at `:95-101`. `OnPvpTimerUpdate(state, expiryTime)` updates both values at `:227-232`; `GetPvpTimer` returns them at `:326-329`.
- Full-client `GetPvpTimer` call inventory: Abyss `abyss/client/validator.py:71-72`; PVP filament `pvpFilaments/client/activation_window/clientPVPfilamentValidator.py:111-112`; voidspace `voidspace/client/client_validator.py:106-107`; random jump `randomJump/client/clientRandomJumpValidator.py:107-108`; timer UI `eve/client/script/ui/crimewatch/crimewatchTimers.py:349`. The four validators reject any state except idle and map it to `PVP_TIMER_ACTIVE` at `abyss/common/validator.py:212`, `pvpFilaments/client/activation_window/clientPVPfilamentValidator.py:176`, `voidspace/client/client_validator.py:173`, and `randomJump/client/clientRandomJumpValidator.py:194`.
- The timer UI gets the pair at `crimewatchTimers.py:347-351`; state 202 displays its expiry at `:383-394`. All `OnPvpTimerUpdate` consumers found in V24.01 are the service `crimewatchSvc.py:227-232`, timer UI `crimewatchTimers.py:384-394`, Abyss controller `eve/client/script/ui/shared/abyss/baseActivationController.py:148-150`, PVP filament controller `pvpFilaments/client/activation_window/pvpFilamentController.py:262-264`, voidspace controller `voidspace/client/void_space_activation_controller.py:201-203`, and random-jump controller `randomJump/client/randomJumpController.py:192-194`. Each controller refreshes or revalidates after the event.
- EveJS 261028.1 `server/src/services/security/crimewatchState.js:3943-3949` updates only the attacking character. eve.rs deliberately differs per the mechanics source.

## Implementation

- `Aggression.Record` now returns a target state with `PvpUntilMs = max(existing, now + 15 minutes)`; it leaves target weapons expiry untouched. The attacker retains its existing weapons and PvP updates for legal and unlawful attacks.
- `CrimewatchRuntime.RecordAggressionAsync` persists the receiver expiry with the same max rule before notifications. After the awaited store update commits, the runtime syncs the attacker and receiver; the receiver gets the client-required two-argument `OnPvpTimerUpdate(202, FILETIME expiry)` event. The existing Host aggression registration in `SpaceComposition` reaches this runtime.
- `GetClientStates` derives the pair from persisted crimewatch state. `ResumeAsync` enumerates active persisted records and syncs the receiver on process start, restoring the timer notification and expiry.
- The receiver gets no weapons timer solely from being shot.

## Verification

- Builds passed: `Eve.Crimewatch`, `Eve.Services.Crimewatch`, `Eve.Services.Space`, and `Eve.Host`, each with `MSBUILDDISABLENODEREUSE=1 dotnet build <project> -p:UseSharedCompilation=false -m:1`.
- PostgreSQL DB `eve_p8` was used through `tools/dev/db-lock.sh`. Focused/full relevant results: `Eve.Crimewatch.Tests` 61 passed; `Eve.Services.Crimewatch.Tests` 53 passed; registered Host crimewatch routes 5 passed, including `Crimewatch_bound_get_client_states_returns_a_persisted_active_receiver_timer`; Space attack-path filters (high-security safety and legal war/target-state cases) 7 passed. All had zero skips.
- The database initially lacked the pinned SDE needed by the Space Host rig. `cargo run -p eve-sde -- ensure` loaded official SDE build 3396210 into `eve_p8`; the same focused Space filters then passed.
- Runtime regressions cover lawful and criminal attacks, receiver timer persistence and wire event, expiry extension, concurrent attacks, expiry notification, null security, and process resume followed by a direct `GetClientStates` call. The registered Host route also returns the persisted active receiver pair as `(202, FILETIME expiry)` and an idle weapons pair.
- Key cases: `AggressionTests.Shooting_a_pilot_in_high_security_starts_the_timers_and_makes_a_criminal_for_fifteen_minutes`, `AggressionTests.What_the_law_allows_is_no_crime_in_any_class`, `AggressionTests.A_later_receiver_pvp_expiry_is_kept_without_a_weapon_timer`; `RuntimeTests.A_crime_in_high_security_tells_the_attacker_its_timers_and_the_system_who_is_flagged_as_the_reference_does`, `A_second_attack_moves_the_timers_on_and_tells_no_flag_again`, `Concurrent_attacks_extend_one_receivers_pvp_timer_without_giving_it_a_weapons_timer`, `A_timer_that_runs_out_is_told_when_it_does_and_the_flag_comes_off_with_the_last_of_them`, `A_ship_shot_in_low_security_makes_a_suspect_and_in_null_security_no_flag`, `What_the_law_allows_runs_the_timers_and_is_no_crime_and_a_listener_hears_every_attack_once`, and `A_process_that_starts_takes_up_the_wait_for_what_still_runs`; Host `Crimewatch_bound_get_client_states_returns_a_persisted_active_receiver_timer`.

## Remaining check

No native graphical client session was launched. The client validators and all of their callers/consumers were checked in V24.01 source, while the database and Host tests verify the active state and expiry sent to that client.
