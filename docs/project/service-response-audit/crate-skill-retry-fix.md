# Crate skill-bundle retry fix

## Scope and evidence status

- Canonical repository: repository root, main, baseline HEAD 52b8421f34e635900a18459da4403f26f2dfaec0.
- 63338f61b6835199e07b7f8234d9cf03910682f4 is an ancestor of that HEAD.
- Changes are limited to CrateService.cs, CrateServiceTests.cs and this document.
- Source inspection independently confirms the failure path. Regression tests were authored before the production fix; pre-fix execution was not performed. The recorded result reports the first post-fix CrateServiceTests run: 16 total, 14 PASS, 2 FAIL at the notification assertion. The assertion correction below is awaiting the parent's rerun.
- The parent owns serial build/test execution and Git mutations. No build, test, commit or push was run by this implementation task.
- Serena was activated at repository root. CBM project C-Users-yutai-eve.rs reports the same absolute source root. Its coverage reports metadata_changed; current method bodies were read with canonical Serena instead of assuming graph freshness.

## Independently confirmed defect

At baseline, CrateService.ClaimSkillBundleAsync removes the bundle and its empty character/crate owner before awaiting ICrateServiceBackend.ClaimSkillBundleAsync. A null result returns PyNone, and an exception escapes, with no restoration. A second identical RPC cannot reach the backend because the pending reward is already gone.

Reproduction specified by the new tests: OpenCrate -> ClaimSkillBundle returns null or throws -> retry the same character/crate/bundle -> the backend must be called again, followed by exactly one successful notification. Under the baseline control flow that retry instead returns PyNone without a second backend attempt.

## Detailed source comparison

One RPC (ClaimSkillBundle) was compared in detail across eight contract points. The other six crate RPCs were not audited in detail in this task. These are source comparisons, not executed EveJS/eve.rs scenarios.

| Contract point | Evidence and decision |
| --- | --- |
| RPC arguments | V24.01 client SkillBundleLootItem.claim sends crateID, skillBundleID. Core reads arguments 0 and 1 in that order. No signature change. |
| Pending ownership | EveJS takePendingSkillBundle uses characterID:crateID and bundle ID. Core retains the same character/crate/bundle ownership and existing positive-ID checks. |
| Reservation and concurrency | Both baseline implementations take pending before grant. Core now reserves one bundle snapshot under the existing pending owner's lock; the backend await happens outside the lock. A single recorded reward cannot be granted by two concurrent calls. |
| Failure and retry | EveJS also removes pending first and can throw during grant. Core intentionally restores on backend null, exception or cancellation via finally. This avoids inheriting the reference's loss path. Null still returns PyNone; exceptions still propagate. |
| Reopening while a claim is pending | Baseline Core replaces the owner map on another open. The existing owner now queues bundle snapshots per ID, so restoring a failed claim does not overwrite or collapse another recorded reward. No new store, schema or backend dependency is introduced. |
| Success response | EveJS returns changedSkillCount and freeSkillPoints. Core preserves those fields and their existing values/types. The client callsite ignores the returned value, so this response itself is not evidence of corruption. |
| Success notification | EveJS emits OnSkillBundleInjected with clientID and changed count / awarded SP. The client consumer takes numberOfSkills, sp. Core preserves the event and payload, after a non-null backend result only. Consumption is marked before notification, so a notification failure does not restore an already granted reward. |
| Next client action | HackingCrateController.ClaimLoot pops special loot before item.claim; ClaimAllLoot and FixedCrateStackController.ClaimSpecialLoot log exceptions without an automatic retry loop. Retaining server pending makes a subsequent RPC retry possible; automatic graphical-client recovery is not established. |

## Absolute source evidence

- ../../../dotnet/src/Eve.Services.Core/CrateService.cs: baseline ClaimSkillBundleAsync lines 204-224; CallAsync dispatch; RememberSpecialLoot; ParseSkillBundles and AddBundle.
- ../../../dotnet/src/Eve.Host/Rpc/InventoryRpc.cs: line 45 registers CrateService with the actual PersistenceCrateServiceBackend and skills/skillCatalog.
- ../../../dotnet/src/Eve.Host/Persistence/Inventory/PersistenceCrateServiceBackend.cs: lines 118-140 return null for missing skill dependencies; skill and free-SP writes share a PostgreSQL transaction; commit precedes the non-null result.
- evejs-v0.12.9/server/src/services/campaign/crateService.js at 828823a75f211fdecb945b087a26c225f111491f: takePendingSkillBundle lines 401-416, applySkillBundleToCharacter lines 454-483, Handle_ClaimSkillBundle lines 665-677.
- eve-logic/eve/client/script/ui/crate/lootItem.py at 1dbb5711a072a146e1d228365668ac05655eefe1: SkillBundleLootItem lines 82-93.
- eve-logic/eve/client/script/ui/crate/controller.py at the same commit: ClaimLoot lines 144-156, ClaimAllLoot lines 158-173, ClaimSpecialLoot lines 327-336.
- eve-logic/eve/client/script/ui/services/skillsvc.py at the same commit: OnSkillBundleInjected lines 1106-1110.

The private Core method has one production caller, CallAsync; this was checked through CBM tracing and Serena references. Public backend signatures are unchanged. Parsing, skill-level clamping, SP normalization and per-open duplicate bundle-ID rejection remain intact. Queued snapshots are not mutated.

## Regression tests and handoff

Eight new cases in five test methods:

1. Backend null / faulted task: pending is restored, retry grants once, response and decoded notification envelope/routing/arguments/sequence are checked, later replay cannot grant again (2 cases).
2. Two overlapping RPCs for one pending reward: one backend call, no notification while awaiting, one notification on success (1 case).
3. An older failed claim is restored after another opening/claim has finished (null / exception; 2 cases).
4. An older failed claim is retained alongside a reopened bundle that is still pending; both recorded rewards remain claimable (null / exception; 2 cases).
5. Other character, crate or bundle IDs cannot consume the rightful pending reward (1 case).

Status: recorded 16 total / 14 PASS / 2 FAIL before the notification assertion correction; rerun after correction is PENDING. No build/test was executed by this implementation task. The existing fake backend gained a controllable skill-claim delegate, following its fitting-claim pattern. TaskCompletionSource gates define the concurrency ordering; waits have five-second timeouts.

Initial static gate: the tracked source/test diff was reviewed and scoped git diff --check passed with exit 0 before the recorded run. The follow-up assertion correction is reviewed through canonical Serena; the unchanged production file is checked by SHA256. The corrected tests await the parent's build/test rerun, and runtime gates remain not performed.

### Notification assertion correction

The failed test built its expected packet with sequence 1. ../../../dotnet/src/Eve.Services/ClientSession.cs initializes _notificationSequence to -1 (line 190) and SendNotification pre-increments it (line 223), so this fresh claim session's first notification has sn=0. The Call fixture creates a new session for the claim; CharacterId's Set only updates attributes and does not advance notification sequence. ClientNotification.Build in dotnet/src/Eve.Services/SessionChanges.cs writes the provided sequence to the sn named payload. The existing AgentMissionStoreTests also expect sequence 0 for a session's first notification.

PyObject is a record with Name and Args; PyTuple and PyDict use structural Seq.Equal in ../../../dotnet/src/Eve.Marshal/PyValue.cs. Thus a whole-packet equality assertion is valid when all fields match; the supported defect here is the incorrect expected sn, not a demonstrated array-reference equality bug. The summarized PyObject display hides that nested difference.

The corrected assertion uses the existing MachoPacket.Decode helper (../../../dotnet/src/Eve.Macho/MachoPacket.cs) to check notification type, Broadcast destination, OnSkillBundleInjected and clientID. It checks the single sn entry equals 0, then uses MarshalCodec.Decode with the same envelope-unwrapping pattern as CharUnboundMgrTests.NotificationArgs to assert the notification body (0, (1, (1, 10))). The outer notification class and envelope shape are checked too. Existing null/throw, no-notification-before-success, retry and no-replay assertions remain intact. The test case count remains 16.

This follow-up changes only CrateServiceTests.cs and this document; CrateService.cs is not edited. A rerun is required before treating the two previously failing cases as PASS.

Recommended parent commands, from repository root, serially:

- dotnet test dotnet/tests/Eve.Services.Core.Tests/Eve.Services.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~CrateServiceTests" --verbosity minimal
- dotnet test dotnet/tests/Eve.Services.Core.Tests/Eve.Services.Core.Tests.csproj --no-restore --verbosity minimal
- git -C repository root diff --check

## Not confirmed boundaries and TODO candidates

- Durable pending ledger: NOT IMPLEMENTED. Pending skill bundles remain service-local memory; server/service restart before claim can lose pending rewards. A future ledger must commit claim consumption with skill/SP grants, using the existing authoritative skill store.
- Ambiguous PostgreSQL commit: a transport/cancellation exception can leave the caller uncertain whether commit succeeded. Restoring a pending reward cannot guarantee exactly-once free-SP grants in that case without a durable claim key. This task does not change the Host transaction or schema.
- Real PostgreSQL failure/restart scenarios, graphical-client recovery and automatic retry, and the full emitSkillSessionState notification sequence were not executed or compared end to end.
- This document records TODO candidates only; it does not mark durable recovery or the entire crate feature complete.
