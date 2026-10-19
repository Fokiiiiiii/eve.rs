# Character phase: work plan

**Verified source:** main at `235ee85a` (2026-10-16), clean working tree.
**Method:** analysis only. No build, no test, no database query, no live client run. Every finding cites a file in eve.rs, the EveJS reference, or the V24.01 eve-logic callsite.
**Layout:** mirrors `docs/project/structures-phase/plan.md` (sections 0 to 7, packages `CH-n`, one haiku agent per package). Rules for every implementer: [agent-rules.md](agent-rules.md), read before starting a package.

## 0. Scope, sources and reading rules

**Scope:** the character domain of the V24.01 client: selection, creation and deletion; appearance and paperdoll; skills (queue, injectors, extractors, remap, plans, notifications, multi-character training, alpha injections); clones (jump clones, clone station, clone grade, implants); attributes; security status; titles, ranks, achievements, tutorial and season data; character info; bounties and standings basics.

**Source priority** (AGENTS.md, DEVIATIONS.md): numbers and attributes from the official SDE (`sde` schema); mechanics from the EVE University wiki, implemented as written; client wire shapes from eve-logic (`/home/user/eve-logic`, V24.01); EveJS v0.12.9 (`/home/user/evejs-v0.12.9/server/src`, short form `services/...` below) is the behaviour reference only. EveJS is not authoritative where it is a stub: `userService.js` `Handle_ActivateCharacterReSculpt` returns `true` and changes nothing. Record each conflict in `docs/reference/compat/DEVIATIONS.md` with the chosen source.

**Reading rules for this plan:**
- "Literal present" means the method name occurs in eve.rs source. It does not prove a working handler. Section 3 gives the real state of each name.
- "Not checked" marks what this analysis did not verify. The package that owns the item checks it first.
- Paths for eve.rs are relative to `/home/user/eve.rs`. Client paths are relative to `/home/user/eve-logic`.

## 1. Shared files, hotspots and conflicts

- `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs`: CH-1, CH-7, CH-12, CH-16.
- `dotnet/src/Eve.Services.Skills/SkillHandlerService.cs` and `SkillHandlerService.Injection.cs`: CH-2, CH-3, CH-6, CH-11, CH-17.
- `dotnet/src/Eve.Services.Core/UserSvcCompatibility.cs`: CH-6, CH-8, CH-10.
- `dotnet/src/Eve.Services.Core/GameRpcServices.cs` (`expertSystemMgr`, `jumpCloneSvc`, `contractProxy`): CH-12, CH-17.
- `dotnet/src/Eve.Host/Rpc/ProgressRpc.cs` (milestones, achievements) and `dotnet/src/Eve.Host/Rpc/CrimewatchRpc.cs` (bounty proxy): CH-5, CH-9, CH-13.
- Host composition: `dotnet/src/Eve.Host/Composition/CharacterServicesComposition.cs`, `CharacterComposition.cs`, `CharacterStores.cs`.
- Postgres: each new table is a new numbered migration in its owning project (`Eve.Characters/Migrations`, `Eve.Skills/Migrations`, `Eve.Npc/Migrations`). Never edit an applied migration.
- GameStore tables `skillPlans`, `characterExpertSystems`, `achievements`, `playerBounties`, `jumpCloneInstallSettlements`, `jumpCloneActivationSettlements` exist in `dotnet/src/Eve.GameStore/GameStoreTables.g.cs`. A search found no eve.rs code that reads or writes them. Use PostgreSQL for new state and do not extend the GameStore for them.
- Parallel packages that touch one hotspot file rebase on `origin/main` before each push (agent-rules).

## 2. Gates: owner decisions

Packages marked GATE cannot start until the owner decides. Each decision is recorded in DEVIATIONS when made.

- **GATE-A, public protobuf gateway (`eve_public.*`).** TODO rows 24 and 856 record owner decision 4: the gateway is not built. eve.rs has 0 public gateway RPC paths; EveJS has 6 (`docs/project/service-response-audit/host-protobuf.md`, counters). Skill plans (CH-21) and titles (CH-22) travel only through it. TODO row 58 records client errors and timeouts on the gateway at startup. Decide: build a minimal transport (CH-20) or keep CH-21 and CH-22 blocked.
- **GATE-B, re-sculpt mechanics.** EveJS does not model it (see the stub above). The item that is consumed, the state that is set and when it ends come from the wiki and the SDE. The owner confirms the rule before CH-8 starts.
- **GATE-C, votes and reports.** `CastUpvote`, `CastDownvote`, `ReportBot`, `ReportISKSpammer` answer `False` in eve.rs (`UserSvcCompatibility.cs`) and `true` in EveJS (`userService.js` 1104-1127). The client does not read the answer (DEVIATIONS). Decide: a feedback store, or a constant `true`. CH-10 implements the choice.
- **GATE-D, player bounty payout.** Which kill claims a placed bounty, and when the payout is due. Source: EVE University wiki (Bounties), then EveJS `services/bounty/playerBountyState.js`. Needed for CH-18 only.
- **GATE-E, faction-warfare rank.** The client reads rank from `facwar` (`fwwarzone/client/dashboard/progression.py:74`), which EveJS serves from `services/faction/facWarMgrService.js`. eve.rs has no faction rank state. Decide whether rank is in this phase (CH-R1, blocked) or in the faction phase.
- **Blocked, outside the phase:** the `charMgr.GetSettingsInfo` signature. The client verifies it with CCP's key (DEVIATIONS, "Character selection and entry"), which eve.rs does not have.

## 3. Verified gaps: each item named in the brief

Verdicts: **Missing** (no working handler), **Stub** (answers a constant or an empty value), **Partial**, **Present** (implemented; see section 5 for the DONE items), **Not needed** (no V24.01 callsite), **Stale** (the doc is wrong).

| # | Item | Verdict | Evidence |
|---|---|---|---|
| 1 | `skillHandler.CheckAndSendNotifications` | Missing | Not in eve.rs. Only client caller: `notifications/client/development/skillHistoryProvider.py:26` (a development provider). Whether production calls it is not checked. CH-3. |
| 2 | `seasonManager.get_season_data_for_character` | Missing | Client: `seasons/client/seasonservice.py` (`_initialize`, `_initialize_season`). eve.rs: `SeasonManagerService` (`SessionStartupRpcServices.cs:101-102`) answers only `process_client_event`, built with no callback (`CoreServices.cs:87`). CH-4. |
| 3 | `ProjectDiscovery.initialize_tutorial_status` | Missing | Client: `projectdiscovery/client/projects/covid/service.py:22-25` (on every character session change). eve.rs has `get_tutorial_completion_status` and the player-state reads (`ProjectDiscoveryService.cs`). CH-4. |
| 4 | `ActivateCharacterReSculpt` | Stub | `UserSvcCompatibility.cs:82-88` answers `False`, consumes nothing. EveJS answers `true`, no effect (`userService.js:1097`). GATE-B, CH-8. |
| 5 | Paperdoll states 1 and 3 | Missing | `CharMgrService.cs:242-246` (comment): only states 0 and 2 are answered (`GetPaperdollState`, line 247). EveJS comment `charMgrService.js:714` names the states. CH-8. |
| 6 | `alphaInjectorMgr.GetNextAvailableInjection` | Stub | `CoreCompatibilityServiceAdapters.cs:25-33` returns `0` (available now) always. Client: `eve/client/script/ui/services/skillsvc.py:1004-1005`. CH-2. |
| 7 | Alpha injections not recorded | Missing | No store. The Daily Alpha Injector exists only in the injector rules (`SkillHandlerService.Injection.cs:57-58`). CH-2. |
| 8 | `userSvc` multi-character training | Partial | Present: slots, active-slot reads, soulbound activation, certificate-backed `ActivateMultiTraining`, and durable slot-expiry queue pausing (TODO row 131). A slot granted before expiry jobs were added has no startup backfill. CH-6. |
| 9 | `DiminishedInjectorSp` correction | Present, acceptance pending | `SkillHandlerService.Injection.cs:55-75`: Large 500k/400k/300k/150k by total-SP tier, Small one fifth of that. Test in `dotnet/tests/Eve.Services.Skills.Tests/SkillServiceTests.cs` (`Injector_preview_uses_per_item_large_and_small_bands_and_non_diminishing_uses`). TODO row 2118 still says pending. CH-2 closes it. |
| 10 | Skill plans | Missing | No plan method in eve.rs. Client plans use the `eve_public` gateway (`skills/skillplan/*`), so MachoNet is not the route. GATE-A, CH-21. |
| 11 | `jumpCloneSvc` | Present | `JumpCloneService` binds and resolves the object and implements the client-used clone reads, naming, destruction, installation, ship offers, and `CloneJump`; `HostJumpCloneStore` persists clone bodies and activation settlements. Host and live tests cover clone jump state, implant swap, fee, cooldown, and restart. Structure Medical service access is checked against the persisted CLONINGBAY_TAX profile setting (setting 23) and access groups; Host coverage verifies allowed and denied members. |
| 12 | Clone station and worldspace | Partial | `cloneStationID` is the home station (`CharUnboundMgrService.cs:333`, `CharMgrService.cs:211`). `worldspaceid` is None. No clone-station field. CH-12. |
| 13 | Implants in the skill bonus and pilot dogma | Partial | Implant and booster modifiers reach the skill attribute view (`SkillHandlerService.cs:94-105`, `SkillAttributeModifier.cs`). No implant effect is applied in `Eve.Services.Dogma` (no `implant` match there). EveJS: `services/dogma/implants/activeImplantModifiers.js`, `services/dogma/brain/providers/implantBrainProvider.js`. TODO row 1087 names the agent access rule as consumer. CH-11. |
| 14 | Titles (`eve_public.cosmetic.character.title`) | Missing | Gateway not built (GATE-A). CH-22. |
| 15 | Character rank (`GetCharacterRankInfo`, `GetCharacterRankOverview`) | Missing, other domain | Served by `facWarMgr` in EveJS and called as `facwar` by the client. GATE-E, CH-R1. |
| 16 | `DeleteCharacter`, `PrepareCharacterForDelete` | Present | Client: `characterSelection.py:848` and `:864`. `CharUnboundMgrService` persists the ten-hour queue, enforces offline ownership and readiness, clears the queue on cancellation, and completes deletion through the Host purge transaction. `CharacterHostTests.Character_deletion_queues_waits_cancels_and_deletes_and_the_deleted_character_is_refused` covers Host routing, restart, refusals, assets, skills, and queue cleanup; `tests/test_character_delete.py` walks prepare, wait refusal, cancellation, and not-queued refusal. CH-1. |
| 17 | `GetCharacterToSelect` | Not needed | Zero V24.01 callsites (eve-logic search). Only EveJS has it. |
| 18 | Achievement tracker fixture | Stub at the edges | Read is real when a record exists (`ProgressRpc.cs:43-46`, `ReadAchievementState`). Without a record it returns `GoldenEventCounts` and `GoldenCompleted` (`LoginInitializationRpcServices.cs:226-229`). `UpdateClientAchievmentsAndCounters` is a no-op (`:220-221`). CH-9. |
| 19 | `SelectCharacterID` population check atomicity | Documented, not re-checked | DEVIATIONS, slice C, "Known gap". The code was not re-read: not checked. CH-7. |
| 20 | Skills mutations and corporation kick/leave flow | Not checked | TODO row 2117 itself calls it a verification item, not a confirmed missing RPC. CH-3 checks the skill part. Corporation part is outside this phase. |
| 21 | `userSvc.CastUpvote` and others answer `False` | Stub | `UserSvcCompatibility.cs:91-104`. Client does not read the answer. GATE-C, CH-10. |
| 22 | `milestoneMgr` | Stub | Host builds `new MilestoneMgrService()` with no handler (`ProgressRpc.cs:50`), so `ProcessCharacterLogon` and `ClaimRewards` answer None. Client: `milestones/client/milestoneSvc.py:22` and `:34`. CH-5. |
| 23 | `expertSystemMgr` | Stub | `GetMyExpertSystems` returns an empty dict (`GameRpcServices.cs:69-75`). `ConsumeExpertSystem` and `RemoveMyExpertSystem` are not in eve.rs. Client: `expertSystems/client/service.py`, `skills/client/skillController.py`, `menucheckers/itemCheckers.py`. CH-17. |
| 24 | Player bounties (`bountyProxy`) | Stub | `BountyProxyKillRightService` is built without `bountyRpcHandler` (`CrimewatchRpc.cs:57-60`). `AddToBounty` refuses ("Bounty service is not wired on this server."). `GetBounties` gives 0 per target (`BountyRows`, `BountyProxyKillRightService.cs:85-105`). Top lists are empty. Client: `eve/client/script/ui/shared/bountySvc.py`. CH-13, CH-18. |
| 25 | Seasons and challenges | Missing | Client calls `select_season`, `get_season_end_time`, `get_challenge`, `get_max_active_challenges`, `claim_challenge_rewards`, `claim_goal_reward`, `fill_up_and_return_challenges_for_character`, `get_character_progress_by_character_id`, `challenge_is_dormant`, `get_challenge_expiration_date`, `get_loot_table_by_typeID` (`seasons/client/*`). CH-4 (read), CH-19 (challenges). |
| 26 | Skill bundle info | Missing in both | Client: `eve/client/script/ui/crate/lootItem.py` calls `skillHandler.GetSkillBundleInfo` through `skillsvc.py:1093`. Neither eve.rs nor EveJS implements it. CH-3. |
| 27 | Certificates | Not needed | `eve/client/script/ui/services/certificateSvc.py` computes certificates on the client from `cfg.certificates`. No RPC. EveJS `certificateMgr` is extra. |
| 28 | Portrait upload, `ConvertAndSavePaperDoll`, `tutorialSvc`, `GetCharCreationInfo`, `GetCharNewExtraCreationInfo`, `GetCareerAgents`, `GetTutorialAgents` | Not needed | No V24.01 callsite (eve-logic search). EveJS-only. |
| 29 | `GetCharacterLockType` and `GetCohortsForUser` / `GetCohortsForCharacter` | Intentional | Answer None and an empty list, as the reference does (DEVIATIONS). Kept. |
| 30 | Security status | Present | Stale DEVIATIONS row 249 says "security status is 0". Section 5, item 6. |
| 31 | Standings | Present, rules not checked | Rowset reads are real (`StandingMgrService.cs:158-208`). NPC kill standing rule not checked. CH-14. |

## 4. Work packages

### 4.1 Summary

Lanes are the PostgreSQL databases from `docs/project/DATABASES.md`: `eve_p2` (live port 26101), `eve_p3` (26102), `eve_p4` (26103). The default `eve` lane is for the main session. Each lane runs its PostgreSQL tests and live run under its own lock.

| ID | Package | Depends on | Wave | Lane | Parallel with |
|---|---|---|---|---|---|
| CH-1 | Character deletion: prepare, cancel, delete | none | 1 | eve_p2 | CH-3, CH-6, CH-10, CH-12 |
| CH-2 | Alpha injections, and acceptance of the injector correction | none | 1 | eve_p3 | CH-5, CH-7, CH-9 |
| CH-3 | Skill handler: CheckAndSendNotifications, GetSkillBundleInfo, skill notification order | none | 1 | eve_p2 | CH-1, CH-6, CH-10, CH-12 |
| CH-4 | Tutorial and season read surfaces | none | 1 | eve_p4 | CH-8, CH-11, CH-13, CH-14 |
| CH-5 | Login milestones (milestoneMgr) | none | 1 | eve_p3 | CH-2, CH-7, CH-9 |
| CH-6 | Multi-character training: item activation, slot expiry | none | 1 | eve_p2 | CH-1, CH-3, CH-10, CH-12 |
| CH-7 | Atomic population check on SelectCharacterID | none | 1 | eve_p3 | CH-2, CH-5, CH-9 |
| CH-8 | Re-sculpt and paperdoll states 1 and 3 | GATE-B | 1 (gated) | eve_p4 | CH-4, CH-11, CH-13, CH-14 |
| CH-9 | Achievement writes, no fixture | none | 1 | eve_p3 | CH-2, CH-5, CH-7 |
| CH-10 | userSvc constant answers (playtime, votes, reports) | GATE-C | 1 (gated) | eve_p2 | CH-1, CH-3, CH-6, CH-12 |
| CH-11 | Implant and booster modifiers in pilot dogma | none | 1 | eve_p4 | CH-4, CH-8, CH-13, CH-14 |
| CH-12 | Jump-clone store, clone state reads, names, destroy, clone-station field | none | 1 | eve_p2 | CH-1, CH-3, CH-6, CH-10 |
| CH-13 | Player bounties: place, read, top lists | none | 1 | eve_p4 | CH-4, CH-8, CH-11, CH-14 |
| CH-14 | Standings verification (NPC kill standing) | none | 1 | eve_p4 | CH-4, CH-8, CH-11, CH-13 |
| CH-15 | Clone installation: station, structure, ship | CH-12 | 2 | eve_p2 | CH-16, CH-17, CH-18 |
| CH-16 | Clone jump, activation fee, clone station on death | CH-11, CH-12, CH-15 | 3 | eve_p3 | CH-19, CH-21 |
| CH-17 | Expert systems | CH-11 | 2 | eve_p3 | CH-15, CH-18, CH-19 |
| CH-18 | Player bounty claims and payout | CH-13, GATE-D | 2 (gated) | eve_p4 | CH-15, CH-17, CH-19 |
| CH-19 | Challenges and season rewards | CH-4 | 2 | eve_p2 | CH-16, CH-17, CH-18 |
| CH-20 | Public gateway: minimal transport and one namespace | GATE-A | 1 (gated) | eve_p4 | any Wave 1 package |
| CH-21 | Skill plans (character) | CH-20 | 3 | eve_p4 | CH-16, CH-22 |
| CH-22 | Character titles and achievement showcase | CH-20, CH-9 | 3 | eve_p4 | CH-16, CH-21 |
| CH-23 | Phase acceptance and documentation sweep | all landed packages | 4 | eve_p2 (combined run) | none |
| CH-R1 | Character rank (faction warfare) | GATE-E | blocked | (none) | out of this phase unless approved |

Wave 1 runs about 14 packages across three lanes. Packages in one wave may run at once on their own lanes; packages in a later wave start when their dependencies have landed on `origin/main`.

### 4.2 Shared acceptance (applies to every package, in addition to its own list)

1. The method is registered in the Host composition and reached through the real service dispatch in a Host test. A direct call to the class is not enough.
2. The answer has the shape the V24.01 client reads. Cite the reader in eve-logic in the test.
3. Persistent state survives a Host restart (a PostgreSQL test with a restart).
4. Notifications go to the recipients and in the order the reference uses (EveJS, then the client's handlers).
5. A live headless test in `tools/headless-client/tests/` passes on the package's lane: `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_pN EVE_LIVE_PORT=2610N tools/dev/db-lock.sh tools/dev/run-eve-live.sh`. A skipped test does not count as passed.
6. The TODO and DEVIATIONS rows that the package touches are updated with the test names. Mechanics that differ from the reference are recorded with the wiki source.

### 4.3 Packages

#### CH-1 Character deletion: prepare, cancel, delete
- **Depends on:** none. **Wave 1, lane eve_p2.**
- **Scope:** `PrepareCharacterForDelete` sets the existing `delete_prepare_at` column. `DeleteCharacter` removes the character only when it is prepared and the waiting period has passed. `CancelCharacterDeletePrepare` (present) clears the state. The selection data shows the queued state. Read the waiting period and the removal set in EveJS (`services/character/characterDeletionRuntime.js`, `charService.js`) and the wiki (character deletion) before coding.
- **Files:** `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs` (near line 428); `dotnet/src/Eve.Characters/Characters.cs`; `dotnet/src/Eve.Host/Rpc/CoreRpc.cs` (construction near line 73); a new migration in `dotnet/src/Eve.Characters/Migrations/` only if removal needs new state; `dotnet/tests/Eve.Services.CharacterSelect.Tests/`; new live test `tools/headless-client/tests/test_character_delete.py`.
- **Sources:** `eve/client/script/ui/login/charSelection/characterSelection.py` 823-871; EveJS files above; wiki.
- **Acceptance:** both methods are registered and answer the client's shape. Prepare persists across a restart. A second prepare, a delete before the waiting period, and a delete of an unprepared character are refused with the reference's text. Cancel clears the state. After delete, the character is absent from `GetCharactersToSelect` and `SelectCharacterID` refuses it. Live: prepare, cancel, prepare, delete under the test clock, login refused.

#### CH-2 Alpha injections, and acceptance of the injector correction
- **Depends on:** none. **Wave 1, lane eve_p3.**
- **Scope:** (a) Record each account's alpha injection and answer `alphaInjectorMgr.GetNextAvailableInjection` as the reference does: `0` when one may be taken now, otherwise the next filetime. Cooldown, Alpha versus Omega, and what the injector grants come from the wiki and the SDE (Daily Alpha Injector). (b) Accept the injector correction already in source (`DiminishedInjectorSp`, section 3 item 9): tier boundaries, quantity, non-diminishing allowances, persisted SP and notification order. Then close TODO row 2118.
- **Files:** `dotnet/src/Eve.Services.Core/CoreCompatibilityServiceAdapters.cs` (`AlphaInjectorMgrService`, lines 25-33; the answer comes from `RpcHandler`, so check the Host wiring); `dotnet/src/Eve.Services.Skills/SkillHandlerService.Injection.cs`; a new migration in `dotnet/src/Eve.Skills/Migrations/`; `dotnet/tests/Eve.Services.Skills.Tests/SkillServiceTests.cs`.
- **Sources:** `eve/client/script/ui/services/skillsvc.py` 982-1010; EveJS `services/_other/alphaInjectorMgrService.js` and the skill injection rules under `services/skills/`; SDE; wiki (skill injectors, alpha clones); CCP "Skill Extractors and Skill Injectors" (cited in TODO row 2118).
- **Acceptance:** a fresh Alpha account reads `0` and can inject. After use the answer is the next filetime and a second injection is refused. The record survives a restart. Omega accounts follow the reference. Live: inject through the client call path and read the answer change. TODO row 2118 closes with the test names.

#### CH-3 Skill handler: CheckAndSendNotifications, GetSkillBundleInfo, notification order
- **Depends on:** none. **Wave 1, lane eve_p2.**
- **Scope:** (a) `skillHandler.CheckAndSendNotifications`: only a development provider calls it (`notifications/client/development/skillHistoryProvider.py:26`). Decide with the owner whether it is dev-only. If it is kept, answer by re-sending the current queue and levels from persisted state, with no duplicates. (b) `GetSkillBundleInfo(skillBundleID)`: needed by the crate loot preview (`eve/client/script/ui/crate/lootItem.py`). The bundle contents come from the SDE or the static data; `ClaimSkillBundle` already exists in the crate service and is the model. (c) Check the notification order in TODO row 2117 for queue save, start, stop, abort and free points.
- **Files:** `dotnet/src/Eve.Services.Skills/SkillHandlerService.cs` (method table); `SkillNotifications.cs`; `TrainingAnnouncer.cs`; `dotnet/src/Eve.Services.Core/CrateService.cs` (bundle source); `dotnet/tests/Eve.Services.Skills.Tests/`.
- **Sources:** eve-logic `lootItem.py`, `skillsvc.py` 1093, `skillHistoryProvider.py`; EveJS `services/skills/` (`Handle_CheckAndSendNotifications`); SDE skill bundle data.
- **Acceptance:** the bound call returns without the unknown-method error. `GetSkillBundleInfo` returns the client's shape for a real bundle and refuses an unknown id. Re-sent notifications do not duplicate. Live: the crate loot preview calls succeed, and the notification order test passes.

#### CH-4 Tutorial and season read surfaces
- **Depends on:** none. **Wave 1, lane eve_p4.**
- **Scope:** (a) `ProjectDiscovery.initialize_tutorial_status`, called on each character session change: answer from the persisted tutorial state, default not complete. (b) `seasonManager.get_season_data_for_character`, `get_season_end_time`, `select_season`, read-only at first. The season data source is decided in this package (EveJS `services/character/seasonManagerService.js` and project data; SDE if it has the data). Challenge calls are CH-19.
- **Files:** `dotnet/src/Eve.Services.Core/ProjectDiscoveryService.cs` (callbacks record; built with none at `CoreServices.cs:85`); `dotnet/src/Eve.Services.Core/SessionStartupRpcServices.cs` (`SeasonManagerService`, from line 101); `CoreServices.cs` (lines 85-87); Host wiring in `dotnet/src/Eve.Host/Rpc/`.
- **Sources:** eve-logic `seasons/client/seasonservice.py` 58-140; `projectdiscovery/client/projects/covid/service.py`; EveJS `services/character/projectDiscoveryService.js`, `seasonManagerService.js`; wiki (Project Discovery, if covered; otherwise DEVIATIONS).
- **Acceptance:** both client startup calls return without errors on a new and on an existing character. The tutorial state is read from storage. The season answer has the shape of the reader in `seasonservice.py`. Host test with the real callbacks. Live: login shows no unknown-method error for these names.

#### CH-5 Login milestones (milestoneMgr)
- **Depends on:** none. **Wave 1, lane eve_p3.**
- **Scope:** `ProcessCharacterLogon` (called on each session change, `milestones/client/milestoneSvc.py:22`) and `ClaimRewards(milestoneID)` (`:34`). Persist per-character milestone progress, the timer and the claimed rewards. Send the timer notification the client expects (`OnMilestoneTimerStarted`, `:25`). Check the rules of EveJS `services/character/milestoneMgrService.js` against the wiki where it covers them; otherwise record them in DEVIATIONS.
- **Files:** `dotnet/src/Eve.Services.Core/MilestoneMgrService.cs`; `dotnet/src/Eve.Host/Rpc/ProgressRpc.cs` (line 50: pass the real handler); a new store and migration in `dotnet/src/Eve.Characters/`; tests in `dotnet/tests/Eve.Services.Core.Tests/`.
- **Acceptance:** the Host passes a real handler, not the default. Logon returns and sends the timer. `ClaimRewards` grants the reward defined by the reference once, and a retry does not grant it again. The claim survives a restart. Live: login, claim, relogin shows it claimed.

#### CH-6 Multi-character training: item activation, slot expiry
- **Depends on:** none. **Wave 1, lane eve_p2.**
- **Scope:** `ActivateMultiTraining` (item certificate): consume the item through the inventory mutation authority (`dotnet/src/Eve.Inventory/Mutation/ItemMutationService.cs`, TODO row 112), grant the slot, and answer the client's shape instead of throwing. Pause a running extra queue when its slot expires (TODO row 131). The 30-day certificate and the Omega requirement follow `ActivateSoulboundMultiTraining`. Check the EveJS `services/user/userService.js` activation and the training-limit rule.
- **Files:** `dotnet/src/Eve.Services.Core/UserSvcCompatibility.cs` (`ActivateMultiTraining`, and the `_multipleTraining` service it calls); `dotnet/src/Eve.Services.Skills/SkillHandlerService.Training.cs` (pause on expiry); `SkillNotifications.cs` (`MultipleCharactersTrainingUpdated`).
- **Sources:** eve-logic `eve/client/script/ui/shared/activateMultiTraining.py`, `menucheckers/itemCheckers.py`; EveJS `services/user/userService.js`, `services/skills/training/skillQueueRuntime.js`; wiki (multiple character training).
- **Acceptance:** activating a certificate consumes exactly one item and grants the slot, with a notification. A second activation of the same slot is refused. A non-certificate item is refused. Slot expiry pauses the extra queue and notifies. The slot survives a restart. Live: activate with a granted item, read `GetMultiCharactersTrainingSlots`.

#### CH-7 Atomic population check on SelectCharacterID
- **Depends on:** none. **Wave 1, lane eve_p3.**
- **Scope:** DEVIATIONS (slice C, known gap): the population check and the session entry are two steps, so two selections into the same full system can both pass. Make check and reservation one step (a lock per system, or a PostgreSQL transaction that holds the count). Do not change the cap of 300 (`SystemPopulation.DefaultCapacity`, `EVE_SYSTEM_POPULATION_CAP`).
- **Files:** `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs` (`SelectCharacterID`); the `SystemPopulation` class (locate by search); `dotnet/src/Eve.Services/CharacterSessions.cs`; tests `PopulationCapTests`.
- **Sources:** DEVIATIONS, "Character selection and entry (slice C)". The reference answers one entry, so there is no EveJS check to copy.
- **Acceptance:** a concurrency test with N parallel selections into a system at cap minus one admits exactly one. The others are refused with the existing error. A failed selection leaves the count unchanged. Live: two accounts select into a full system, with the cap set by `EVE_SYSTEM_POPULATION_CAP`.

#### CH-8 Re-sculpt and paperdoll states 1 and 3 (GATE-B)
- **Depends on:** GATE-B. **Wave 1 once decided, lane eve_p4.**
- **Scope:** `userSvc.ActivateCharacterReSculpt(item)` consumes the re-sculpt item (type from the SDE, rule from GATE-B) and sets the recustomization state. `charMgr.GetPaperdollState` then answers 1 (re-sculpting), and 3 for a full recustomization. `UpdateExistingCharacterFull` and `UpdateExistingCharacterLimited` clear the state after a save. Check whether `UpdateAppearanceAsync` resets it.
- **Files:** `dotnet/src/Eve.Services.Core/UserSvcCompatibility.cs` (82-88); `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs` (`GetPaperdollState`, 242-260); `dotnet/src/Eve.Host/CharacterRpcServices.cs` (`UpdateAppearanceAsync`, line 128); a state column in a new `Eve.Characters` migration; the inventory consume path.
- **Sources:** eve-logic `menucheckers/itemCheckers.py`, `eve/client/script/ui/shared/item.py`, `menusvc.py`, `eve/client/script/ui/services/ccSvc.py`; EveJS `services/character/charMgrService.js` (line 714), `paperDollServerService.js` (229-290). Do not copy `userService.js` line 1097 (stub). Wiki (re-sculpt, character customization).
- **Acceptance:** a valid item is consumed once, the state is persisted as 1, and `GetPaperdollState` returns 1. A save returns the state to 0. An invalid or missing item returns `False` with no change. The state survives a restart. Live: activate, read the state, save.

#### CH-9 Achievement writes, no fixture
- **Depends on:** none. **Wave 1, lane eve_p3.**
- **Scope:** `UpdateClientAchievmentsAndCounters` is a no-op (`LoginInitializationRpcServices.cs:220-221`). Persist the counters and completed achievements to PostgreSQL, using `ReadAchievementState` as the read side. Remove the golden fallback (`:226-229`): a character with no record gets empty dicts. Definitions and completion rules come from EveJS `services/achievement/achievementRuntime.js` and `achievementState.js`, checked against the wiki where it covers them.
- **Files:** `dotnet/src/Eve.Services.Core/LoginInitializationRpcServices.cs` (`AchievementTrackerMgrService`, 128-235); `dotnet/src/Eve.Host/Rpc/ProgressRpc.cs` (43-50); `LoginInitializationGameStoreProjection.ReadAchievementState`; a new `Eve.Characters` table and migration (the GameStore `achievements` table is unused).
- **Sources:** eve-logic `achievements/client/achievementSvc.py` (`achievementTrackerMgr` calls); EveJS `services/achievement/*`, `services/character/achievementTrackerMgrService.js`.
- **Acceptance:** a new character gets empty dicts. An update persists and is read back after a restart. A completed achievement is not counted again on a retry. Tests must not assert the golden fixture. Live: the tracker call shows the update.

#### CH-10 userSvc constant answers (playtime, votes, reports)
- **Depends on:** GATE-C. **Wave 1 once decided, lane eve_p2.**
- **Scope:** `GetSessionPlaytimeMinutes` answers `0` (`UserSvcCompatibility.cs`, `OverrideUserCallAsync(call, new PyInt(0))`). Its source is the session or character store; find the client callers first. Votes and reports follow GATE-C: a feedback store with duplicate handling, or the constant `true` the reference uses.
- **Files:** `dotnet/src/Eve.Services.Core/UserSvcCompatibility.cs` (90-130).
- **Sources:** eve-logic callers of `CastUpvote`, `ReportBot`, `ReportISKSpammer` (`customersatisficationcont.py`, `menuUtil.py`, `characterInfoWindow.py`); callers of `GetSessionPlaytimeMinutes` (not found by search: check); EveJS `services/user/userService.js` 1104-1127.
- **Acceptance:** each method answers in the client's shape. If GATE-C chooses a store, a record exists and a repeated vote is handled. `GetSessionPlaytimeMinutes` equals the stored minutes. Live: call each through the headless client.

#### CH-11 Implant and booster modifiers in pilot dogma
- **Depends on:** none. **Wave 1, lane eve_p4.**
- **Scope:** implants (flag 89, category 20; `SkillHandlerService.cs` 19-21 and 191) and boosters add attribute modifiers to the pilot in the Dogma service, not only to the skill view (`GetCharacterAttributeModifiers`). Apply them through the existing dogma modifier path. Refresh on fit and removal with the notifications the reference sends (`OnServerBrainUpdated`, `OnModuleAttributeChanges`). If the agent access rule reads skill bonus (TODO row 1087), include it.
- **Files:** `dotnet/src/Eve.Services.Dogma/CharacterDogmaSync.cs`, `DogmaInfo.cs`; `dotnet/src/Eve.Services.Skills/SkillAttributeModifier.cs`, `SkillHandlerService.cs` (94-105); the item-move hook in `Eve.Inventory`; tests in `dotnet/tests/Eve.Services.Dogma.Tests/`.
- **Sources:** EveJS `services/dogma/implants/implantRuntime.js`, `activeImplantModifiers.js`, `services/dogma/brain/providers/implantBrainProvider.js`, `services/skills/boosters/boosterSkillRuntime.js`; SDE (implant attributes); wiki (implants, boosters).
- **Acceptance:** fitting an implant with an attribute bonus changes the attribute that dogma reads, and the training rate. Removal reverts it, with notifications. Restart keeps it. Live: fit, read the attribute, remove.

#### CH-12 Jump-clone store, clone state reads, names, destroy, clone-station field
- **Depends on:** none. **Wave 1, lane eve_p2.**
- **Scope:** new PostgreSQL tables for installed jump clones (name, location, implants), the ship clone slot, the last clone jump time and the character's clone station (distinct from the home station). Replace `JumpCloneService` (`GameRpcServices.cs:88-94`) with a real service. Answer `GetCloneState`, `GetStationCloneState`, `GetShipCloneState`, `GetNumClonesInPilotsStructure`, `ValidateInstallJumpClone` (read), `SetJumpCloneName`, `DestroyInstalledClone`, `ResetLastCloneJumpTime`, and bind and resolve as EveJS does. Worldspace stays None unless the client needs it.
- **Files:** `dotnet/src/Eve.Services.Core/GameRpcServices.cs` and a new file beside it; a new migration in `dotnet/src/Eve.Characters/Migrations/`; `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs` (`cloneStationID`, 333); `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs` (211); the registration list in `CoreServices.cs`; tests `dotnet/tests/Eve.Services.Core.Tests/GameRpcServicesTests.cs` (update the existing jump-clone tests).
- **Sources:** eve-logic `eve/client/script/ui/services/clonejumpsvc.py` (`lm.*`, `GetClones`, `GetShipClones`, `GetStationClones`, `HasCloneReceivingBay`); `eve/client/script/ui/shared/neocom/charsheet/jumpClonesPanel.py`; EveJS `services/station/jumpCloneService.js`, `jumpCloneRuntime.js` (state builders), `jumpCloneRules.js`; wiki (jump clones).
- **Acceptance:** a new character has no installed clones. The state payloads match the client readers. `SetJumpCloneName` persists and refuses a clone the character does not own. `DestroyInstalledClone` removes the record. State survives a restart. The bind test asserts the bound node. Live: open the jump clone screen calls and read the state.

#### CH-13 Player bounties: place, read, top lists
- **Depends on:** none (the wallet and the kill-right store exist). **Wave 1, lane eve_p4.**
- **Scope:** give `BountyProxyKillRightService` a real handler (Host: `CrimewatchRpc.cs:57-60`). Implement `AddToBounty` (debit the placer once, retry-safe, store the bounty), `GetBounties` (the stored amount per target), `GetMyBounties`, `GetBountiesAndKillRights`, `SearchCharBounties`, and the pilot, corporation and alliance top lists and hunter lists, built from stored bounties. Store in PostgreSQL (the GameStore `playerBounties` table is unused). Claims and payouts are CH-18.
- **Files:** `dotnet/src/Eve.Host/Rpc/CrimewatchRpc.cs`; `dotnet/src/Eve.Services.Crimewatch/BountyProxyKillRightService.cs`; the debit path in `dotnet/src/Eve.Wallet/`; a new migration in `Eve.Crimewatch` (or `Eve.Npc`); tests in `dotnet/tests/Eve.Services.Crimewatch.Tests/`.
- **Sources:** eve-logic `eve/client/script/ui/shared/bountySvc.py` (`ProxySvc('bountyProxy')` at 83, 148, 203-219) and `bountyWindow.py`; EveJS `services/bounty/bountyProxyService.js`, `bounty/playerBountyState.js`; wiki (bounties).
- **Acceptance:** placing a bounty debits the wallet once, including on retry, stores it, and returns the client's shape. `GetBounties` returns the stored amount, not 0. Top lists are ordered as the reference orders them. Insufficient ISK is refused. Restart keeps the bounties. Live: place a bounty and read it back.

#### CH-14 Standings verification (NPC kill standing)
- **Depends on:** none. **Wave 1, lane eve_p4. Verify first: this package may close as DONE.**
- **Scope:** the `standingMgr` reads are implemented. Verify the standing rules the character sees: NPC kill standing (EveJS `services/character/factionKillStandingRuntime.js`, `npcStandingsAuthority.js`, `standingRuntime.js`). Agent and mission standing gain belong to the agent domain and are not in this package. Decide with the owner whether faction kill standing is in this phase.
- **Files:** `dotnet/src/Eve.Services.Corporations/StandingMgrService.cs`; the NPC kill hook in `dotnet/src/Eve.Npc/`; tests.
- **Sources:** eve-logic `eve/client/script/ui/services/standingsvc.py`, `shared/neocom/charsheet/standingsPanel/standingsPanel.py`; the EveJS files above; wiki (standings).
- **Acceptance:** an NPC kill changes the character's standing by the rule, and the rowset shows it. Repeated kills follow the rule. Restart keeps it. Live: kill an NPC and read `GetCharStandings`. If the rule already holds, record the test names and close the row.

#### CH-15 Clone installation: station, structure, ship
- **Depends on:** CH-12. **Wave 2, lane eve_p2.**
- **Scope:** `InstallCloneInStation` and `InstallCloneInStructure` (the clone is installed at the current location; the structure variant follows the reference's rights rule). `OfferShipCloneInstallation`, `AcceptShipCloneInstallation`, `CancelShipCloneInstallation`. `GetPriceForClone`. Charge the price once from the wallet and record the settlement in PostgreSQL (the GameStore `jumpCloneInstallSettlements` shape).
- **Files:** as CH-12, plus `dotnet/src/Eve.Wallet/` (charge) and `dotnet/src/Eve.Ships/` (ship clone slot).
- **Sources:** eve-logic `clonejumpsvc.py` (`OfferShipCloneInstallation`, `DestroyInstalledClone`, `GetPriceForClone`); EveJS `services/station/jumpCloneRuntime.js` (install, offer, accept, cancel), `jumpCloneRules.js`; wiki.
- **Acceptance:** installing charges once and creates the clone where the character is docked. The offer, accept and cancel follow the reference's notification order. Insufficient ISK is refused. Restart keeps the clone. Live: install, read state, destroy.

#### CH-16 Clone jump, activation fee, clone station on death
- **Depends on:** CH-11, CH-12, CH-15. **Wave 3, lane eve_p3.** Split into two agents if it exceeds one session: the jump, then the death.
- **Scope:** `CloneJump(cloneID, ...)` moves the character to the clone's location, replaces its implants with the clone's implants, honours the cooldown from the last jump, and charges the activation fee (the GameStore `jumpCloneActivationSettlements` shape, in PostgreSQL). A capsule death (`dotnet/src/Eve.Ships/CharacterShips.Death.cs`) sends the character to its clone station, falling back to the home station, with the reference's text.
- **Files:** the jump clone service (CH-12); `dotnet/src/Eve.Ships/CharacterShips.Death.cs`; `dotnet/src/Eve.Services.Space/` (session change for the jump); `dotnet/src/Eve.Services.CharacterSelect/CharacterLogoff.cs` (clone station in the session change); tests.
- **Sources:** EveJS `services/station/jumpCloneRuntime.js` (`performCloneJump` and death sections only); eve-logic `clonejumpsvc.py` (`CloneJump`, `ResetLastCloneJumpTime`); wiki (jump clones, clone station, death).
- **Acceptance:** a clone jump moves the character, installs the clone's implants and starts a new cooldown. A jump during cooldown is refused. The fee is charged once. A death after the jump respawns the character at its clone station. Restart keeps the state. Live: jump and read the location and implants; kill the test character and check the respawn.

#### CH-17 Expert systems
- **Depends on:** CH-11 (the modifier path). **Wave 2, lane eve_p3.**
- **Scope:** `expertSystemMgr.GetMyExpertSystems` (now an empty dict), `ConsumeExpertSystem` (item consumption, then a timed expert system), `RemoveMyExpertSystem`. An active expert system raises skill levels in the skill read and in dogma (through CH-11's path). Expiry is a scheduler job. Notifications: `OnExpertSystemsUpdated`. Types and attributes from the SDE; catalogue from EveJS `services/skills/expertSystems/expertSystemCatalog.js`.
- **Files:** `dotnet/src/Eve.Services.Core/GameRpcServices.cs` (69-75, replace); a new store and migration (shape of the GameStore `characterExpertSystems`); a scheduler job (pattern in `dotnet/src/Eve.Scheduler/`); `dotnet/src/Eve.Services.Skills/`; `dotnet/src/Eve.Services.Dogma/`.
- **Sources:** eve-logic `expertSystems/client/service.py`, `skills/client/skillController.py`, `menucheckers/itemCheckers.py`, `eve/client/script/ui/services/menusvc.py`; EveJS `services/skills/expertSystems/*.js` (runtime, catalogue, expiry scheduler, notifications); SDE; wiki (expert systems).
- **Acceptance:** consuming an expert system grants it, notifies, and raises the skill level in the skill read and in dogma. Removal and expiry revert it and notify. Restart keeps the active timers. Live: consume, then read `GetMyExpertSystems`.

#### CH-18 Player bounty claims and payout (GATE-D)
- **Depends on:** CH-13 and GATE-D. **Wave 2, lane eve_p4.**
- **Scope:** a kill of a pilot with a placed bounty, by a claimant with the right GATE-D names, claims the bounty. The payout falls due after the reference's delay. Pending payouts are kept in PostgreSQL (pattern: `dotnet/src/Eve.Npc/PgBountyStore.cs`). The victim's bounty is reset.
- **Files:** the kill hook in `dotnet/src/Eve.Services.Space/` (pattern: `SpaceSessions.Npc.cs`); the scheduler job in `dotnet/src/Eve.Host/Composition/SpaceComposition.cs` (`space.bounty.pay`, line 369); `dotnet/src/Eve.Services.Crimewatch/`.
- **Sources:** EveJS `services/bounty/bountyRuntime.js`, `bounty/playerBountyState.js`, `bounty/killRightState.js`; wiki (bounties, kill rights).
- **Acceptance:** a kill of a bountied pilot creates one pending payout. It pays once when due. Retries and a restart create no duplicate. Notifications are sent. Live: kill a test pilot, with the existing live kill helper if one exists (not checked).

#### CH-19 Challenges and season rewards
- **Depends on:** CH-4. **Wave 2, lane eve_p2.**
- **Scope:** the challenge calls of `seasonManager` that the client makes: `get_challenge`, `get_max_active_challenges`, `fill_up_and_return_challenges_for_character`, `get_character_progress_by_character_id`, `challenge_is_dormant`, `get_challenge_expiration_date`, `claim_challenge_rewards`, `claim_goal_reward`, `get_loot_table_by_typeID`, `get_total_needed_xp`. Progress comes from `process_client_event`, which is not wired yet. Rewards are granted through the wallet or the inventory.
- **Files:** `dotnet/src/Eve.Services.Core/SessionStartupRpcServices.cs` (`SeasonManagerService`); `ProjectDiscoveryService.cs` (callbacks); a new store and migration; Host wiring; tests.
- **Sources:** eve-logic `seasons/client/seasonservice.py`, `challengeinfopanelprogressbar.py`, `rewardIcon.py`; EveJS `services/character/seasonManagerService.js`, `projectDiscoveryService.js`; the challenge data source (EveJS data, origin not checked).
- **Acceptance:** the challenge list has the client's shape for a character. Progress from `process_client_event` persists. A claim grants the reward once and refuses a second claim. Restart keeps the progress. Live: read the challenges and claim one.

#### CH-20 Public gateway: minimal transport and one namespace (GATE-A)
- **Depends on:** GATE-A. **Wave 1 if approved, otherwise blocked. Lane eve_p4.**
- **Scope:** an HTTP/2 listener for the client's public gateway: `Requests/Ping`, `Requests/Send`, `Notices/Consume`, `Events/Publish`, mirroring EveJS `_secondary/express/publicGatewayLocal.js` (lines 2564-2591). The envelope and the error shape come from the V24.01 client (`publicGateway/publicGatewaySvc.py`). One namespace is the first producer: the one CH-21 needs. The other namespace candidates stay as TODO (TODO row 2123).
- **Files:** a new listener under `dotnet/src/Eve.Host/` (`Hosting/`); the protobuf modules as the client ships them (`eve_public.*_pb2`); tests.
- **Sources:** eve-logic `publicGateway/publicGatewaySvc.py`, the `eve_public.*_pb2` modules, `skills/skillplan/*`; EveJS `_secondary/express/publicGatewayLocal.js`, `gatewayServices/index.js`; `docs/project/service-response-audit/host-protobuf.md` (gateway sections).
- **Acceptance:** an HTTP/2 client gets Ping. `Requests/Send` for the first namespace round-trips the client's protobuf bytes, decoded by the V24.01 `_pb2` module. An unknown namespace gets the reference's error. Live: the headless client reaches Ping over the same path, if it can speak HTTP/2 (not checked).

#### CH-21 Skill plans (character)
- **Depends on:** CH-20. **Wave 3, lane eve_p4.**
- **Scope:** `eve_public.character.skill.plan` (create, delete, get active, list) and `.milestone` (create, delete, get all). Plans are stored per character in PostgreSQL (shape of the GameStore `skillPlans`). Plans do not train skills. The analytics namespace (`eve_public.app.eveonline.skill.plan.certified.analytics_pb2`) and corporation plans (`corpSkillPlans`) are out of this phase.
- **Files:** the gateway handler (CH-20); a new store and migration in `dotnet/src/Eve.Skills/`; tests.
- **Sources:** eve-logic `skills/skillplan/*` (loggers, UI, protobuf); EveJS `services/skills/plans/skillPlanState.js`, `_secondary/express/gatewayServices/skillPlanGatewayService.js` (its fields not checked); wiki (skill plans).
- **Acceptance:** create, list and delete a plan and a milestone through the gateway. Plans survive a restart. The training projection does not change. Live: the gateway client creates and reads a plan.

#### CH-22 Character titles and achievement showcase
- **Depends on:** CH-20, CH-9. **Wave 3, lane eve_p4.**
- **Scope:** `eve_public.cosmetic.character.title.api` (owned, equip, equipped for character) and `.definition.api` (latest). Title definitions come from EveJS `services/achievement/data/titles.json` and `latest.chti`. Owned titles come from the achievements (CH-9). The equipped title is stored per character. The showcase namespace (`eve_public.achievement.showcase.api`) is in scope only if CH-20 supports it.
- **Files:** the gateway handler; the achievement store (CH-9); `dotnet/src/Eve.Characters/` (equipped title); tests.
- **Sources:** eve-logic `cosmetics/client/character/title/*` (27 files reference it), `achievements/client/*`; EveJS `services/achievement/achievementGatewayProto.js`, `data/titles.json`.
- **Acceptance:** a character that has earned a title sees it owned. Equip stores the choice. The equipped title appears in public info only if the reference does (check). Restart keeps it. Live: through the gateway.

#### CH-23 Phase acceptance and documentation sweep
- **Depends on:** every package that landed. **Wave 4, lane eve_p2 for the combined run; eve_p3 and eve_p4 for their own packages.**
- **Scope:** one live walk per flow: login, select, create, delete; skills (queue, injector, extractor, remap, alpha); clones (install, jump, destroy); implants; expert systems; milestones; achievements; tutorial and season start; bounties; standings. Update the TODO rows named in section 3 and the stale DEVIATIONS rows in section 7. Do not edit `docs/project/README.md` or any other existing README.md (agent-rules).
- **Acceptance:** the combined live run exits 0 with no skips on eve_p2, and the per-lane runs pass on eve_p3 and eve_p4. Each closed TODO row cites its test. DEVIATIONS holds every new deviation with its source.

#### CH-R1 Character rank (faction warfare), blocked
- **Depends on:** GATE-E. **Not started in this phase unless the owner approves.**
- **Scope:** `GetCharacterRankInfo`, `GetCharacterRankOverview` and `GetMyCharacterRankInfo` on `facWarMgr` (EveJS `services/faction/facWarMgrService.js`). eve.rs has the enlistment service (`dotnet/src/Eve.Services.Core/FactionActivityRpcServices.cs`) but no faction rank state.

## 5. Verified as already DONE (do not redo)

Each item was checked in source; the test names are those found by search. Items marked "not re-run" were not executed in this analysis.

1. **Selection and entry.** `charUnboundMgr.SelectCharacterID`, `GetCharacterSelectionData`, `GetCharactersToSelect`, population cap of 300 per system with alternative systems, docked refusal (DEVIATIONS, "Character selection and entry (slice C)"; `PopulationCapTests`). Atomicity is CH-7.
2. **Creation and names.** `CreateCharacterWithDoll`, `ValidateNameEx`, `GetValidRandomName`, `UpdateCharacterGender`, `UpdateCharacterBloodline`, `GetQAStarterSystemIDs` (`dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs`, lines 124-251). The client does not call `GetCharCreationInfo` (item 28).
3. **Home station.** `charMgr` `get_home_station`, `set_home_station`, `get_next_remote_change_time`, `GetHomeStationRow`; one resolver shared with the alias (DEVIATIONS, "Effective home-station read surfaces"; 55 PostgreSQL CharacterInfo tests per that row).
4. **Character info and public info.** `GetPublicInfo`, `GetPublicInfo3`, `GetCharacterDescription`, `SetCharacterDescription`, `GetCharacterCreationDate`, `SaveCharacterSetting`, `GetCharacterSettings`, `LogSettings` (`dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs`). `GetSettingsInfo` is blocked (section 2).
5. **Contacts, notes, owner notes, labels, blocked owners.** Literal present in `charMgr`, backed by the `characterNotes` read model (`dotnet/src/Eve.Host/Persistence/CharMgr/PersistenceCharMgrReadModel.cs`). Not re-run live.
6. **Security status.** Read from the relation store (`dotnet/src/Eve.Host/Composition/CharacterComposition.cs:107`) into `charMgr.GetPublicInfo` (`CharMgrService.cs:141`), `charUnboundMgr` (`CharUnboundMgrService.cs:542`) and `crimewatch` (`CrimewatchService.cs:128`). Penalties (`SecurityStatusPenalties.cs`, `SecurityPenalty.cs`; `SecurityPenaltyTests`, 6 facts) and the NPC recovery (`NpcSecurityStatusRecoverySubscriber.cs`; `NpcSecurityStatusRecoveryTests`, 9 facts). DEVIATIONS row 249 ("security status is 0") is stale.
7. **NPC bounties.** `NpcBounty.cs`, `PgBountyStore.cs` with migration `0001_bounties.sql`; paid by the scheduler job `space.bounty.pay` (`SpaceComposition.cs:369`); `NpcBountyTests` (10 facts), `PgBountyStoreTests`, `NpcBountyHostTests`.
8. **Kill rights.** `GetMyKillRights`, `GetKillRightsOnCharacters`, `SellKillRight`, `CancelSellKillRight` in `BountyProxyKillRightService.cs`, backed by the crimewatch PostgreSQL store (`BountyProxyKillRightServiceTests`, 2 facts). Not re-run live. The bounty side is CH-13.
9. **Training and queue.** `SaveNewQueue`, `CharStartTrainingSkill`, `CharStartTrainingSkillByTypeID`, `CharStopTrainingSkill`, `AbortTraining`, and the queue and history reads (`SkillHandlerService.Training.cs`). Notifications in `SkillNotifications.cs` and `TrainingAnnouncer.cs`. Test: `Queue_free_skill_points_follow_saved_order_and_apply_with_the_balance` (`dotnet/tests/Eve.Services.Skills.Tests/SkillServiceTests.cs`).
10. **Injectors and extractors.** `ExtractSkills` (test `ExtractSkills_consumes_an_extractor_and_creates_an_injector_after_removing_exactly_500000_points`), `Injector_notifies_committed_inventory_then_free_balance_and_failed_retries_are_silent`, `CombineSkillInjector`, `SplitSkillInjector`, `CheckInjectionConstraints`, `GetDiminishedSpFromInjectors`. Only the acceptance of the correction is open (CH-2).
11. **Alpha restriction on extraction.** `Alpha_clones_can_extract_only_omega_restricted_skills`; `SkillCatalog.AlphaMaxLevel` by race (`dotnet/src/Eve.Skills/SkillCatalog.cs`). Alpha injections are CH-2.
12. **Remap and respec.** `RespecCharacter`, `GetRespecInfo`; the test `RespecCharacter_persists_attributes_uses_the_timed_remap_first_and_notifies_the_client`; the remap rules in `dotnet/src/Eve.Skills/PgSkillStore.cs` (99 points, timed interval of 365 days). Attributes are DONE.
13. **Free skill points.** `ApplyFreeSkillPoints`, `ApplyFreeSkillPointsToQueue`, `ApplyFreeSkillPointsToSkills`, `GetFreeSkillPoints*` (same test as item 9).
14. **Skill purchase.** `PurchaseSkills`, `GetDirectPurchasePrice`, `IsSkillAvailableForPurchase`. Present, not re-run live.
15. **Skill reads.** `GetSkills`, `GetAllSkills`, `GetSkillPoints`, `GetSkillHistory`, `GetAttributes`, `GetBoosters`, `GetImplants`, `GetCharacterAttributeModifiers`, `GetMySkillInfo`, `GetSkillChangesForISIS`. Present (the implant effect on dogma is CH-11).
16. **Omega and clone grade.** The account end time (`identity.accounts.subscription_end_at`), `OmegaLicenses`, `subscriptionMgr` answers, `GetCharOmegaDowngradeStatus`, `ToggleValidation`, and the Alpha rule of `multiLoginBlocker` (`dotnet/src/Eve.Services.CharacterSelect/MultiLoginBlockerService.cs`). Tests: `OmegaStateTests` (6 facts), `SubscriptionTransitionsTests`, `ValidationAndOmegaTests`, `MultiLoginBlockerTests` (DEVIATIONS, "Character selection and entry" and its Omega rows).
17. **Logoff.** `userSvc.UserLogOffCharacter` returns the character to the selection screen on the same connection (DEVIATIONS, slice D).
18. **Character energy.** `characterEnergyMgr.GetMyEnergyState` reads `characterEnergyState` (`dotnet/src/Eve.Host/CharacterRpcServices.cs:15-24`; `CompatibilityRpcServiceTests`).
19. **Paperdoll reads and appearance update.** `GetPaperDollData`, `GetPaperDollDataBatch`, `GetPaperDollPortraitDataFor`, `GetMyPaperDollData` (`CharacterRpcServices.cs`, lines 57-80). `UpdateExistingCharacterFull` and `...Limited` go to `UpdateAppearanceAsync` (line 128), which writes through the character store. Whether it resets the state is CH-8.
20. **Achievement read and HasEverWarped.** `HasEverWarped` through its callback (`ProgressRpc.cs:47`). Writes are CH-9.
21. **Tutorial completion read.** `ProjectDiscovery.get_tutorial_completion_status`, `get_player_state`, `get_player_statistics` (`ProjectDiscoveryServiceTests`). The startup call is CH-4.
22. **Standings reads.** `standingMgr` `GetCharStandings`, `GetCorpStandings`, `GetNPCNPCStandings`, `GetStandingTransactions`, `GetStandingCompositions` return rowsets (`dotnet/src/Eve.Services.Corporations/StandingMgrService.cs`, 158-208). The rules are CH-14.
23. **Session and entry notifications.** `OnCharacterSelected` with no arguments, the first-select notification and the docked ship timing (DEVIATIONS rows 143 to 146).

## 6. Not in this phase

- **Faction-warfare rank** (`facWarMgr`): GATE-E, CH-R1.
- **Corporation skill plans** (`corpSkillPlans`) and corporation goals: corporation phase and the gateway.
- **Ship skins** (`shipCosmeticsMgr.GetEnabledCosmetics` answers an empty dict): adjacent cosmetics domain, owner to assign.
- **Owner groups** (`ownerGroupManager`, used by `accessGroupsController.py`): structure and corporation domain.
- **EveJS-only, no V24.01 callsite** (recorded so nobody builds them): `certificateMgr` (client computes certificates), `tutorialSvc` and the NPE tutorial runtime (`npe/tutorialRuntime.js`; eve-logic mentions `tutorialSvc` only in `marshalstrings.py`), `photoUploadSvc`, `ConvertAndSavePaperDoll`, `GetCharacterToSelect`, `GetCharCreationInfo`, `GetCharNewExtraCreationInfo`, `GetCareerAgents`, `GetTutorialAgents`, `GetTutorials`, `GetTutorialsAndConnections`. The `LogStarted`, `LogCompleted`, `LogAborted` and `LogClosed` names in EveJS are not checked against the client.
- **EveGuard, bannedwords, Kiring redeem codes** (`ShouldKickCheater`, `fetch_regexes`, `ActivateRedeemingCode`): anti-cheat and store domains.

## 7. Stale documents, and what was not checked

### 7.1 Stale rows to correct in CH-23
- **DEVIATIONS row 249** ("security status is 0 and there are no bounties"): security status is real (section 5, item 6). Bounties are CH-13.
- **DEVIATIONS row 261** (`charUnboundMgr` methods "other than the ones the screen needs"): it lists `GetCharacterInfo`, `GetQAStarterSystemIDs`, `UpdateCharacterGender` and `UpdateCharacterBloodline`, which eve.rs answers (section 5, item 2). Rewrite the row to list only what is missing: `PrepareCharacterForDelete`, `DeleteCharacter`, `GetCharCreationInfo` (no client callsite).
- **TODO row 2118** (injector): close after CH-2 with its test names.
- **TODO row 131** (multi-character training): update after CH-6.
- **TODO row 2117** (skill mutations and corporation flow): stays a verification item; CH-3 checks the skill part.

### 7.2 Not checked by this analysis
- No build, no test, no database query, no live client run. Nothing here is a pass.
- The SDE was not queried. Item types for re-sculpt, Daily Alpha Injector, expert systems and implants are for the package agents to confirm.
- The EVE University wiki was not fetched. Every mechanic in CH-1 to CH-22 is to be read from the wiki by the package agent before coding; where the wiki is silent, EveJS is the second source and the choice goes in DEVIATIONS.
- EveJS was read by grep and by the listed bodies only. Its handlers were not executed.
- The V24.01 client call sites come from grep. A call built from a computed name would not be found. The eve-logic repository was searched as text.
- DEVIATIONS was read in lines 151-161, 239-264, 877-901 and 1318-1392, and searched by keyword elsewhere. The file has 4,151 lines; rows in other sections may hold character items this plan does not list.
- TODO.md (2,559 lines) was searched by keyword, not read in full. `service-response-audit/evejs-only-responses.md` was consulted only through the TODO references.
- Not checked in code: the `SelectCharacterID` atomicity (item 19 of section 3), the body of `GetRecentShipKillsAndLosses`, the source of `GetSessionPlaytimeMinutes`, the NPC kill standing rule, the source of the challenge data, and the existing live kill helper for CH-18.
- Test counts above are the number of test attributes found by search, not results of runs.
