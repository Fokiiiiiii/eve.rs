# CI red: character selection notification order (2026-11-09)

## Finding

Main CI run `42630209868` failed in the live headless tests after the character-selection implementation began sending the account subscription-time notice first. The recorded-walk test and the race-parametrized skill comparison still expected `OnCharacterSelected` to be the first notification and removed only one extra notification before comparing the recorded session and skill packets.

This was a stale test expectation, not a server regression. EveJS v0.12.9 sends `OnSubscriptionTimeUpdateServer` as the first notification in every `SelectCharacterID`, before the session change (`server/src/services/character/charService.js:1543-1545`). The eve.rs Host test pins the resulting notification sequence and subscription value (`dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:74-82`); `CharUnboundMgrService` sends the subscription notice at selection entry and later sends `OnCharacterSelected` (`dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs:331,426`).

The V24.01 client calls `SelectCharacterID` as part of `PerformSessionChange` (`eve/client/script/ui/login/charSelection/characterSelection.py:705-714`). It scatters `OnCharacterSelected` after a relevant `charid` session change (`eve/client/script/parklife/gameui.py:433-455,504-505`), and its PLEX panel handles `OnSubscriptionTimeUpdateServer` by refreshing the Omega-time display (`eve/client/script/ui/shared/neocom/charsheet/plexPanel.py:160-163`).

## Change

The headless assertions now require the exact notification prefix `OnSubscriptionTimeUpdateServer`, then `OnCharacterSelected`, and remove both notices before comparing the recorded session and skill packets. This keeps the recording comparison intact while checking the client-visible selection order directly. No server behavior changed.

## Verification

- `tools/dev/run-eve-live.sh` with `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4`, `EVE_MARKET_SEED=skip`, and only `test_the_recorded_walk_replays` plus `test_a_new_character_has_the_skills_the_reference_gives`: 5 passed, 0 skipped.
- `Eve.Host.Tests.CharacterHostTests.Host_character_selection_sends_the_subscription_time_before_the_session_change` against `eve_p4`: 1 passed, 0 skipped.
- The same five cases were attempted against EveJS v0.12.9. The recorded-walk case failed earlier at `achievementTrackerMgr.HasEverWarped` (`True`, expected `False`). All four race cases reached and passed their selection-notification assertions, then failed on later skill-handler/history/skill-total differences. These unrelated failures were not changed or masked.

Not checked: graphical-client interaction or full EveJS parity beyond the notification assertions described above. The relevant V24.01 and EveJS callsites were inspected, and the successful headless live tests exercised the registered eve.rs Host and wire notifications.
