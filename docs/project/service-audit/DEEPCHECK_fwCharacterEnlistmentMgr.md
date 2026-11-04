# Deep check: fwCharacterEnlistmentMgr

## Whole-client call paths

The six registered methods all appear in eve/client/script/ui/services/fwEnlistmentSvc.py. GetMyEnlistment is also consumed by modularHangarBehaviours.py:157-162, which reads only the effective faction slot and treats None as not enlisted.

| Method | Request and reply consumed by V24 |
|---|---|
| GetMyEnlistment | no args; fwEnlistmentSvc.py:79-83 destructures (effectiveFactionID, directFactionID, corpFactionID); nullable IDs are cached. |
| GetCorpAllowedEnlistmentFactions(corporationID) | one positional corporation ID; :102-103 returns the faction ID list, also used by corp_ui_home.py:412 and factionalWarfare/enlistmentChecks.py:71. |
| SetMyCorpAllowedEnlistmentFactions(factionIDs) | one positional list; :105-107 returns the saved list then emits local OnCorpAllowedEnlistmentFactionsSet. |
| GetMyDirectEnlistmentCooldownTimestamp | no args; :121-122 returns FILETIME long, cached for the UI timer. |
| CreateMyDirectEnlistment(factionID) | one positional faction ID; :92-94 ignores reply; V24 refreshes enlistment after the request. |
| RemoveMyDirectEnlistment | no args; :96-100 ignores reply; V24 refreshes enlistment and cooldown. |

The corporation allowed-faction list is also read by corp_ui_direct_enlistment_mgt.py:80,113 and infosvc.py:1232. A whole-tree search found no other service callers.

## Host owner and transitions

CoreRpc.cs:107-124 registers callbacks into the shared character and corporation stores. FactionActivityRpcServices.cs:90-183 reads character direct faction/cooldown and corporation faction/policy; create and remove save the character owner’s state before changing session warfactionid. Removal sets a one-day cooldown using FILETIME ticks. Policy updates persist through CorporationStore and echo the saved IDs.

Create/Remove produce session-change notifications only after a successful persistent write. CharacterFactionRpcHostTests.cs:62-156 exercises all six calls over a real Host socket, checks persisted callback state across follow-up reads, and observes two session-change notifications. The host test injects in-memory callbacks; production composition points those callbacks at PostgreSQL stores.

EveJS v0.12.9 implements the same tuple/list/FILETIME/None shapes and changes warFaction session state after create/remove in server/src/services/corporation/fwCharacterEnlistmentMgrService.js:70-143. It stores fallback character and corporation records in process memory. Eve.rs uses shared character/corporation persistence instead. Client-supplied list/tuple faction IDs accept integer wire values; other values are ignored by the faction parser.

## Errors and limits

Malformed or missing faction arguments normalize to zero; missing character/corporation session identity leaves the operation unchanged and returns None. Store writes that report failure cause no session mutation. The service does not invent a UserError where the client implementation expects the nullable/no-op reply. GetMyEnlistment returns [corp faction, None, corp faction] when no direct enlistment exists.

No PostgreSQL database or graphical V24 client was used for this audit. The success path and session changes were checked by the existing Host wire test, but restart persistence and concurrent writes were not exercised in this task.
