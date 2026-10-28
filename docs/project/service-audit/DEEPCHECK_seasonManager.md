# Deep check: seasonManager

## Whole-client calls and wire use

The client uses seasonManager only through seasons/client/seasonservice.py and achievements/client/eventHandler.py, with one Insider tooling call. Normal challenge and reward UI calls happen only after the client sees active season data.

| Method | V24 call and consumed reply |
|---|---|
| get_season_data_for_character | seasonservice.py:268-285; None means no active season; otherwise the season object is cached. |
| select_season(seasonID) | seasonservice.py:108-109, selected season is refetched afterward. |
| get_character_progress_by_character_id | seasonservice.py:99-100 and :284-285; KeyVal fields include character_id, season_id, seasonal_points, max_seasonal_points, seasonal_goals, next/last goal, challenges. |
| get_challenge(challengeID) | seasonservice.py:112-119 and infoPanels/infoPanelSeasons.py:155,178; cached challenge object or None. |
| get_max_active_challenges | seasonservice.py:130-131; numeric cap. |
| challenge_is_dormant(challengeID) | seasonservice.py:136-139 and contentPageSeasons.py:462-480; bool controls visibility. |
| get_challenge_expiration_date(challengeID) | seasonservice.py:141-142, challengeinfopanelprogressbar.py:58,107 and challengeEntry.py:119; nullable timestamp. |
| get_season_end_time | seasonservice.py:144-150; FILETIME used for countdown. |
| get_loot_table_by_typeID(typeID) | seasonservice.py:166-167; list of loot entries for next_seasonal_goal.reward_type_id. No active-season caller reaches it when progress has no next goal. |
| fill_up_and_return_challenges_for_character | seasonservice.py:363-376; a progress object whose challenges populate the UI. |
| claim_challenge_rewards(challengeID, clickOrigin) | seasonservice.py:404-407 and challengeinfopanelprogressbar.py:127 / challengeEntry.py:229; return ignored, then UI refreshes. |
| claim_goal_reward(goalIndex) | seasonservice.py:553-555 and rewardIcon.py:335; bool gates client-side claimed state and OnSeasonalGoalClaimedInClient. |
| process_client_event(eventType, extra_data=..., amount=...) | achievements/client/eventHandler.py:107-119; called only when seasonService reports an active season and an interesting event. Reply ignored. |
| reload_fsd_data | eve/devtools/script/insider.py:1527-1528; developer-only reload action, bool reply is not used. |

The event request has a positional event type and the exact keyword names extra_data and amount. eventHandler adds typeID and, where applicable, objectID before the call.

## Host behavior and EveJS comparison

CoreServices.cs:99 registers SeasonManagerService. With no active-season definitions it returns None for season selection/data/challenge, zero maximum active challenges, true for dormant, None expiry, empty challenge/progress maps, empty loot list, false for reward claims, and a current FILETIME season end. Progress preserves the expected KeyVal field names and no-season sentinels.

process_client_event passes event type, optional extra_data, and optional amount to an injected progression callback. Without a callback it raises the exact CustomInfo UserError with “Season progression is unavailable.” and sends no notification. EveJS v0.12.9 returns empty/no-season shapes too, but its event method emits OnChallengeProgressUpdate and its challenge-claim method emits OnChallengeRewardsGranted while returning false. Those notifications are fake progress/reward signals without active season state; eve.rs deliberately does not send them. The no-season data limits are recorded in DEVIATIONS.md.

## Tests and limits

ClientVerifiedNSHostWireTests.cs:66-79 covers no-season wire shapes; :84-108 checks positional eventType and keyword field transport through the Host; :110-124 asserts the CustomInfo refusal when no progression provider exists. ProjectDiscoveryHostTests.cs also exercises season defaults through the registered service. The ClientVerifiedNS test uses a callback only for the active event wire path; there is no production season progression provider yet.

No database or graphical V24 client was used for this audit. No service code or tests were changed in this documentation group.
