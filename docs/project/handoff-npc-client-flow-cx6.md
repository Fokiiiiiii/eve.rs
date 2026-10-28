# NPC combat client flow handoff

The `tools/headless-client/tests/test_npc.py` walk now asserts the NPC ball arrives before the rat's `otheradd` lock, special FX, incoming damage message, and the pilot ship's damage-state update. The kill walk checks the pilot's `add` lock, weapon activation, capacitor attribute updates, hit, ball removal, bounty notice, wreck appearance, and opened loot inventory in client-observable order.

V24.01 evidence: Michelle buffers destiny state at `eve/client/script/remote/michelle.py:1098-1104`, projects module and damage updates at `1528-1545`; TargetMgr consumes `OnTarget` at `eve/client/script/parklife/targetMgr.py:586-598`, completes target locks at `1272-1317`, and marks another entity as targeting the pilot at `1161-1188`. Godma routes module attributes at `eve/client/script/environment/godma.py:365-367,1341-1361` and damage messages at `502-549`. The bounty adapter consumes `OnBountyAddedToPayout` at `notifications/client/bountyNotificationAdapter.py:15-22`.

EveJS v0.12.9 activates NPC hostile modules in `server/src/space/npc/npcBehaviorLoop.js:1275-1302`, emits `OnTarget` from `server/src/space/runtime/scene/targeting.js:212-227`, and creates native NPC wrecks in `server/src/space/npc/nativeNpcWreckService.js:858-917`. Recorded EveJS weapon and module walks remain the cross-server checks for player activation and capacitor behavior; the NPC AI walk is EveRS-specific.

Local checks: Python compilation passed. The three live NPC cases were skipped because no live server was configured; no PostgreSQL database was used. Actions verification and graphical-client acceptance remain open.
