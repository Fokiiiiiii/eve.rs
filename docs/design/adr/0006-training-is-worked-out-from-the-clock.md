# ADR 0006: Skill training is worked out from the clock and settled when something asks

- Status: accepted
- Date: 2026-09-29
- Decided by: the repository owner's direction (ADR 0001); the details are the implementer's.

## Context

A skill gains points every minute for days. The client shows a skill's points, a queue with the time each line starts
and ends, and is told when a level finishes, whether it is logged in or not. The reference keeps the queue as data,
computes points from the time the running line began, and settles finished lines whenever a read asks and from a timer.

## Decision

1. **No state ticks.** A queue is `(lines, running, when the running line began)`. What a line will be worth at any moment
   (points gained, when it ends, when the next begins) is a pure function of that and the skills (`Eve.Skills.Training`:
   `Project`, `PointsAt`, `Settle`, `Rebase`). Nothing is written while a skill trains.
2. **Settling writes what the clock finished.** `CharacterTraining.SettleAsync` turns finished lines into trained levels, moves
   the queue on and adds to the history, in one transaction under the character's lock. Every read that shows skills
   (`skillHandler`, the ship for dogma) settles first, so a level is never late for the person who looks. It raises
   `Settled` afterwards; a handler that fails does not undo the settlement.
3. **Changes carry the points.** Saving a queue whose first line differs, or stopping, writes the points the running skill has
   gained into the skill (its level follows from them), so the skill resumes from where it was.
4. **Telling the client is the adapter's job** (`Eve.Services.Skills`): `TrainingAnnouncer` sends the events in the reference's
   order, asks `ICharacterDogmaSync` (implemented in `Eve.Services.Dogma`) for the ship's attributes that changed, the character's
   attributes and the brain, and arms one timer per character for the running line's end. A character that is not in the game
   is not told; its next login settles quietly, as the reference does. `CharacterSessions` finds the session that plays a
   character.
5. **Rules of points follow the reference and the game**: a level costs `ceil(250 * rank * 2^(2.5 * (level - 1)))`; a skill trains at
   its primary attribute plus half its secondary, times 1 for Omega (0.5 otherwise) and the server's speed multiplier
   (`EVE_SKILL_TRAINING_SPEED`, also carried by the learning attributes the client sees, as the reference does).

## Consequences

- Correctness of time is testable without waiting: unit tests move a clock, and the differential tests run both servers at a
  speed 60,000 times faster, where a queue finishes in a second.
- After a restart nothing needs restoring: the queue is data, and the first read or login settles it. A timer exists only to
  tell a client that is watching.
- Alpha clone limits, extra training slots, injectors and remapping are not there yet (`docs/reference/compat/DEVIATIONS.md`, "Not yet matched").
