# Insurgency FOB reinforcement timing

The campaign end-to-end test used to destroy an insurgency FOB after three hours. That interval did not match either the V24.01 client or EveJS; the test had copied an unused, incorrect value in `Eve.Insurgency.InsurgencyRules`.

## Evidence

- The V24.01 client `structures/structure.py:95-115` returns `REINFORCE_DURATION_ARMOR_DEFAULT` for armor reinforcement and applies the structure reinforce schedule and size-based exit jitter. `structures/const.py:151` sets the default to 86,400 seconds and lines 169-171 set the default jitter to 10,800 seconds.
- V24.01 `structures/structure.py:225-234` excludes insurgency FOBs from hull reinforcement. Its FOB-specific check does not change armor reinforcement. The `REINFORCE_DURATION_ARMOR_INSURGENCY_FOB` constant is also 86,400 seconds (`structures/const.py:159`).
- EveJS `server/src/services/structure/structureConstants.js:199-214` records the same defaults. `structureState.js:2662-2684` resolves non-Medium armor timers to 86,400 seconds, and `:4214-4249` mirrors the client FOB armor/hull eligibility.
- The campaign creates FOBs with no selected reinforcement weekday and hour 0 (`Eve.Services.Space/Insurgency/InsurgencyDirector.cs:122`); the shared structure timer then persists the actual reinforcement end time.

## Change

Removed the unused 2-to-4-hour helper and its unit assertions from `InsurgencyRules`. The space end-to-end test now verifies that a FOB remains reinforced after three hours, advances to its persisted timer deadline, and then completes the armor and hull damage path. Shared structure timer rules remain the single owner of reinforcement timing.

The evidence is from the V24.01 client source and EveJS source. The EveJS runtime could not be started in this environment because its `better-sqlite3` native binding is unavailable; no graphical client run was made.
