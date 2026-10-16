# Handoff S7 (CH-16 clone jump, S4 gaps, CH-20..22; database lane eve_p3, live port 26102)

Worktree `/home/user/wt-ch-s7`, branch `ch-s7`. Push: `git fetch origin main && git merge origin/main`, then `git push origin HEAD:main`.

## Status

| Item | State | Verified (eve_p3, nothing skipped) |
|---|---|---|
| 1. Whole .NET solution compiles; Services.Core tests | DONE (eaf70523) | `dotnet build dotnet/EveRs.slnx`: 0 errors; `Eve.Services.Core.Tests` 741 passed, 0 skipped (with `EVE_PG_URL` on eve_p3) |
| 2. CH-16 clone jump, session relocation helper | IN PROGRESS | |
| 3. S4 gaps | NOT STARTED | |
| 4. CH-20 gateway, CH-21 skill plans, CH-22 titles | NOT STARTED | |

## Decisions (read before changing)

- Clone jump: only station clones exist (InstallCloneInStructure and ship installs are refused), so the jump target kind is `station`.
- Implants: the body's implants go to the replacement clone at the origin (wiki: implants stay with the old body); the target clone's implants become the body's (new item rows at the implant flag).
- Cerebral accelerators (booster slot 10) stay active on a jump and a death (DEVIATIONS S4).
- Death respawn: the home station (unchanged by a jump; EveJS `cloneStationID` is only set by a home station change).
- Fee: 900,000 ISK, wallet reference 128 (JUMP_CLONE_ACTIVATION), no counterparty (same shortcut as the install, DEVIATIONS).
- Cooldown: 24 h less the Infomorph Synchronizing level (skill 33399), checked when the target is not the current location.

## Risks

- Test runs need `EVE_PG_URL='Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p3'` through `tools/dev/db-lock.sh`; without it the mining ledger tests fail on an assert (not a skip).
