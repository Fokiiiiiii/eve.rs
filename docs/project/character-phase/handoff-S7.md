# Handoff S7 (CH-16 clone jump, S4 gaps, CH-20..22; database lane eve_p3, live port 26102)

Worktree `/home/user/wt-ch-s7`, branch `ch-s7`. Push: `git fetch origin main && git merge origin/main`, then `git push origin HEAD:main`.

## Status

| Item | State | Verified (eve_p3, nothing skipped) |
|---|---|---|
| 1. Whole .NET solution compiles; Services.Core tests | DONE (eaf70523) | `dotnet build dotnet/EveRs.slnx`: 0 errors; `Eve.Services.Core.Tests` 741 passed, 0 skipped (with `EVE_PG_URL` on eve_p3) |
| 2. CH-16 clone jump, session relocation helper | IMPLEMENTED, tests pending | Host `CharacterHostTests.Clone_jump_moves_the_body...` passed (1, eve_p3, before the lapse tests were added); live `test_clone_jump_live.py` written, not yet run against a live host; death path not changed (respawn at home station, not tested as a live kill) |
| 3. S4 gaps | PARTIAL | Booster lapse durable job `skills.boosterLapse` (`SkillBoosterLapse`) and expert lapse persistent notice (type 253, misc group, written offline) implemented; Host tests `A_skill_booster_lapses...` and `An_expert_system_that_lapses...` were failing at the last run (job did not change state; diagnosis open). Not done: host-restart test for implants/expert systems, fitting check audit (grants are in the ShipSnapshot world, no test added). |
| 4. CH-20 gateway, CH-21 skill plans, CH-22 titles | NOT STARTED | Host is a plain library/console app with a custom Macho TCP listener; no HTTP/2 or gRPC code. Needs a Kestrel (Microsoft.AspNetCore.App shared framework, h2c) listener, gRPC framing and the `eve_public` protobuf codecs. |

## Decisions (read before changing)

- Clone jump: only station clones exist (InstallCloneInStructure and ship installs are refused), so the jump target kind is `station`.
- Implants: the body's implants go to the replacement clone at the origin (wiki: implants stay with the old body); the target clone's implants become the body's (new item rows at the implant flag).
- Cerebral accelerators (booster slot 10) stay active on a jump and a death (DEVIATIONS S4).
- Death respawn: the home station (unchanged by a jump; EveJS `cloneStationID` is only set by a home station change).
- Fee: 900,000 ISK, wallet reference 128 (JUMP_CLONE_ACTIVATION), no counterparty (same shortcut as the install, DEVIATIONS).
- Cooldown: 24 h less the Infomorph Synchronizing level (skill 33399), checked when the target is not the current location.

## Risks

- Test runs need `EVE_PG_URL='Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p3'` through `tools/dev/db-lock.sh`; without it the mining ledger tests fail on an assert (not a skip).
