# ST-18 handoff: customs inspection roll, standing loss, durable penalty job, fine ref type

Package ST-18: the four "Not done" items of ST-16 (`handoff-ST-16.md`). Worktree `wt-st18` from origin/main e33d21f6; database `eve_p2` (`tools/dev/db-lock.sh`). TODO section 59; decisions and sources: DEVIATIONS "Structure ST-18 (2026-10-16)".

## What was built

1. **Inspection roll (ST-16 1).** `UndockContraband.ApplyAsync` rolls against the pilot's smuggling chance (dogma attribute 445 after its implant and skill modifiers, `SkillHandlerService.SmugglingChanceAsync`). Roll below chance: evaded, nothing charged or removed. The roll is `UndockContraband.InspectionRoll(character, ship, system, time)`, a deterministic hash in [0, 1).
2. **Standing loss (ST-16 2).** The SDE `standingLoss` per rule is summed over the settled items and applied through `RelationStore` (relation `standing`, faction as object) with the EveJS raw-change formula (`UndockContraband.StandingAfter`). The history reason is the EveJS standing event 126 JSON.
3. **Durable penalty job (ST-16 3).** `contraband.penalty-settle` on `DurableScheduler`. Inspection schedules it in its own transaction; `DurableScheduler.RunNowAsync` (new) runs it at once; the handler settles fine, standing, confiscation and the `contraband_penalties` row in one job transaction. Registered by `UndockContraband.LoadAsync`; the Host calls it from `SpaceComposition.LoadCustomsPenaltyAsync`, before the simulation gate, so any Host process has the handler.
4. **Fine ref type (ST-16 4).** Ref type 51 (eve-logic `refContrabandFine`), counterparty and reference the faction (EveJS `ownerID2`/`referenceID`).

## Verified (eve_p2)

- Space `UndockContraband` 6/6 (the penalty, durable restart, evasion, seeded roll, standing formula).
- Skills `Smuggling_chance` 1/1 and `Booster_activation` 1/1 (filter run: 2/2).
- Host `CustomsPenaltyHostTests` 1/1: the Host's own runner settles a scheduled penalty once.
- Host `StructureOfficeTests` 10/10 in the same run (before the sim-gate move; the wide run below repeats it).
- Build: Space tests, Skills tests and Host tests build with 0 errors.

## Not done

- Smuggling chance with a real SDE implant effect (the test uses an in-test implant with attribute 9950).
- Graphical client: the standing window, the fine's journal line, the removal notice.
- Failed inline run: the undock goes on and the removal notice waits for the next inventory sync (shortcut in DEVIATIONS ST-18).
- Settlement is one transaction; EveJS settles in checkpointed steps.

## Things that will bite

- `grep`, `cat` are denied in Bash: searches go through Serena, CBM, or `python3 -I`.
- The customs load is no longer inside the sim block. Anything that adds a space-only input must be a field of `CharacterServicesComposition`.
- `Eve.Services.Space` now references `Eve.Relations` (ProjectReference).
- Penalty tables live in the `contraband` schema (`EVE_CONTRABAND_SCHEMA`).
