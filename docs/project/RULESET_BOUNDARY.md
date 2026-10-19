# Ruleset boundary

Target profile: client V24.01 build 3396210. Owner decision Q-16 adopts `24.01@2026-06-16` per item using the supplied report timeline. The build-to-SDE date mapping remains a report claim; the battlefield correction follows the specific 2026-07-01 patch boundary.

## After-checkpoint changes: do not apply

| Change in supplied report | Checkpoint treatment | Current-code check / follow-up |
|---|---|---|
| 2026-06-19 Military Campaign and NPC attribute adjustments | after-checkpoint: do not apply | Confirm relevant NPC/static inputs against pinned SDE; TODO added. |
| 2026-07-01 FW Battlefield LP 50,000 to 150,000; freighter removal | after-checkpoint: exclude the increase; apply checkpoint value: 50,000 LP. | `FwBattlefields.LpPool` is 50,000, following the per-item owner decision Q-16 and report 2 patch timeline. The 150,000 value was introduced after the checkpoint. |
| 2026-07..08 Rogue Drone Fabricator waves, AoE immunity and respawn | after-checkpoint: do not apply | No targeted code comparison completed. |
| 2026-08-19 mission backend data-processing update | after-checkpoint: do not apply | `AgentMissionService` is implementation; mission source data needs version check. |
| 2026-08-25 Python 3 migration | after-checkpoint; regression-test lesson only | No game rules adopted. |
| 2026-09-08 donation wallet information exposure | after-checkpoint as a game update; security regression lesson may be adopted | See SECURITY_STATUS and TODO. |
| 2026-09-22 Ansiblex capacitor 1,250 TJ, recharge and distance zones; Insurgency/FW changes | after-checkpoint: do not apply | No checkpoint-specific Ansiblex value comparison completed. |
| 2026-10 Ansiblex, sovereignty and engine updates | after-checkpoint: do not apply | No game rules adopted. |

## Pre-checkpoint changes to verify in current data

| Report item | Candidate checkpoint rule | Action |
|---|---|---|
| 2026-06-12 Visions of Greatness NPC weakening, orbit/damage change, Tristan supply | applies | Compare pinned SDE and AIR tutorial pack; arc evidence and missing NPC IDs/dungeon mapping/time limits recorded in reconciliation. |
| 2026-06-16 simulation-mission capacitor recharge time x0.5 | applies; change recharge TIME, not capacitor amount | Check mission simulation dogma data; TODO. |
| 2026-06-16 Pyroxeres grade-0 yield 50% | applies | Check pinned ore composition; TODO. |
| 2026-06-16 NPC mass adjustments | applies | Compare pinned SDE and relevant runtime profiles; TODO. |

Additional pre-checkpoint report findings: 2026-06-09 Cradle of War, Exordium, Achievements/Titles, Command Carriers, Military Campaigns; 2026-06-10 Visions of Greatness; 2026-03-18.1 CRAB Carrier variant and belt deployment restriction. Apply only where matching client/SDE evidence supports them.
