# Ruleset boundary

Target profile: client V24.01 build 3396210. Owner decision Q-16 adopts `24.01@2026-06-16` per item using the supplied report timeline. The mapping from build 3396210 to the June 16 checkpoint remains a report claim. CCP’s official [Version 24.01 patch notes](https://www.eveonline.com/news/view/patch-notes-version-24-01) independently confirm the dated changes listed below; post-checkpoint entries are excluded from this profile.

## After-checkpoint changes: do not apply

| Change in supplied report | Checkpoint treatment | Current-code check / evidence |
|---|---|---|
| 2026-06-19 Military Campaign objective/payout and Visions of Greatness NPC adjustments | after-checkpoint: exclude | CCP’s 2026-06-19.1 notes apply campaign adjustments to objectives revealed from June 20 and record additional Visions NPC attribute tweaks. They postdate the adopted June 16 boundary. Keep June 16 NPC values; see the pinned SDE for static attributes. |
| 2026-07-01 FW Battlefield LP 50,000 to 150,000; freighter removal | after-checkpoint: exclude the increase and removal; apply checkpoint values: 50,000 LP and the prior spawn rules. | `FwBattlefields.LpPool` is 50,000. CCP’s 2026-07-01.1 notes explicitly state the 150,000 increase and freighter removal; both postdate the checkpoint. |
| 2026-07..08 Rogue Drone Fabricator wave, AoE immunity, NPC respawn and spawn-stagger changes | after-checkpoint: exclude | CCP’s 2026-07-23.1, 2026-07-28.1, 2026-07-30.1, 2026-08-05.1 and 2026-08-20.1 notes date these changes after the checkpoint. No Rampant Drone Fabricator runtime is present in the Host/Rust gameplay trees; no later wave or damage rule is imported. |
| 2026-08-19 agent mission backend data-processing update | after-checkpoint: exclude | CCP’s 2026-08-19.1 note names backend data processing but publishes no player-facing formula. Keep the June 16 mission data and formula behavior; do not infer a rules change from the backend note. |
| 2026-08-25 Python 3 migration | technical update after checkpoint; no game rule applies | CCP’s 2026-08-25.1 notes classify it as a technical update. No gameplay rule is adopted. |
| 2026-09-08 client vulnerability fix | after-checkpoint as a client update; security regression lesson may be applied independently | CCP’s 2026-09-08.1 notes classify it as a technical fix. Security coverage is tracked in SECURITY_STATUS and TODO, outside this gameplay ruleset. |
| 2026-09-22 Ansiblex capacitor and distance zones; Insurgency/FW changes | after-checkpoint: exclude | CCP’s 2026-09-22.1 notes give the 1,250 TJ capacity, recharge and distance zones and enumerate the Insurgency/FW changes. No values from that update are applied to build 3396210. |
| 2026-10 Ansiblex, sovereignty and engine updates | after-checkpoint: exclude | The supplied report dates these after the checkpoint. The cited 24.01 patch page is maintained through September 22 in the checked official page; October details were not independently reconciled here and no October rule is adopted. |

## Pre-checkpoint changes to verify in current data

| Report item | Candidate checkpoint rule | Action |
|---|---|---|
| 2026-06-12 Visions of Greatness NPC weakening, orbit/damage change, Tristan supply | applies | Compare pinned SDE and AIR tutorial pack; arc evidence and missing NPC IDs/dungeon mapping/time limits recorded in reconciliation. |
| 2026-06-16 simulation-mission capacitor recharge time x0.5 | applies; change recharge TIME, not capacitor amount | Check mission simulation dogma data; TODO. |
| 2026-06-16 Pyroxeres grade-0 yield 50% | applies | Check pinned ore composition; TODO. |
| 2026-06-16 NPC mass adjustments | applies | Compare pinned SDE and relevant runtime profiles; TODO. |

Additional pre-checkpoint report findings: 2026-06-09 Cradle of War, Exordium, Achievements/Titles, Command Carriers, Military Campaigns; 2026-06-10 Visions of Greatness; 2026-03-18.1 CRAB Carrier variant and belt deployment restriction. Apply only where matching client/SDE evidence supports them.
