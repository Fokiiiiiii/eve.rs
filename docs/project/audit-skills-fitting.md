# Skills, fitting, and dogma audit

Audit scope: skill-point rates and queue transitions; skill prerequisites; Omega and cerebral-accelerator expiry; fitting, onlining, ammo, and hull restrictions; dogma modifiers and ship-condition wire values. Client evidence is from `/home/user/eve-logic` V24.01; EveJS evidence is from `/home/user/evejs-261108.1` v0.12.9.

## Confirmed bugs fixed

### Queue projections rewrote training that had already elapsed

**Verdict: confirmed with the fix.** Queue projection previously used one current rate for the entire active line. A later Omega purchase or a cerebral accelerator expiring could therefore change earlier SP and completion estimates. Rates now use persisted Omega entitlement frames and booster-expiry boundaries. Training point estimates, queue projections, settlement, and rebasing use the same time-varying schedule. Subscription end-time changes and their history frames commit in one account transaction; training changes commit skills, queue, and history together under the character lock. `GetAttributes` refreshes the learning bonus after restart, and expired booster rows remain available to settle the historical rate until the durable lapse job rebases and prunes them.

Evidence: V24.01's rate is primary plus half secondary in `/home/user/eve-logic/characterskills/util.py:28-29`; its training calculator accounts for booster expiries in `/home/user/eve-logic/characterskills/skill_training.py:114-137`. EveJS evaluates the rate at each instant and walks rate boundaries in `/home/user/evejs-261108.1/server/src/services/skills/training/skillTrainingMath.js:290-347`, and projects every queue line through that calculator in `skillQueueRuntime.js:559-634`. The client requests `GetSkillQueueAndFreePoints` and persists queue changes through `SaveNewQueue` in `/home/user/eve-logic/eve/client/script/ui/services/skillQueueSvc.py:117-154`.

Implementation path: `SkillHandlerService.GetSkillQueueAndFreePoints` -> `CharacterTraining.ViewAsync/SettleAsync` -> `Training.Project/Settle`; queue writes go through `SkillHandlerService.SaveNewQueue` -> `CharacterTraining.SaveQueueAsync` -> `PgTrainingStore.SaveAsync`. Omega history is appended with the subscription update in `PgAccountDirectory`; booster history is read from `PgSkillStore` and lapse is durable through `SkillBoosterLapse`.

End-to-end coverage: `Registered_omega_purchase_does_not_retroactively_double_alpha_skill_training`, `Registered_skill_queue_projects_each_line_across_cerebral_accelerator_expiry`, and `Registered_cerebral_accelerator_refreshes_training_attributes_without_a_running_queue` exercise registered RPCs, notifications, PostgreSQL state, and restart. The account-directory tests also cover concurrent subscription extensions and history consistency.

### Skill requirements were checked at fit time instead of online time

**Verdict: confirmed with the fix.** A module whose skill prerequisites are missing can be fitted offline; attempting to bring it online is refused and leaves it offline. `FittingRules.Check` now handles fit legality without conflating it with online eligibility. `CharacterShips.SetOnlineAsync` still checks required skill levels before committing the online state.

Evidence: V24.01 sends `SetModuleOnline` from `/home/user/eve-logic/eve/client/script/dogma/clientDogmaLocation.py:698-710`; the fitting screen's `FitFitting` request is sent from `/home/user/eve-logic/eve/client/script/environment/fittingSvc.py:592-619`. EveJS explicitly preserves an unskilled fit offline in `/home/user/evejs-261108.1/server/src/services/ship/shipService.js:582-585` and checks skill prerequisites in `dogma/dogmaService/moduleOperation.js:2638-2667`. The registered Host test `Registered_invbroker_fits_module_without_required_skills_and_leaves_it_offline` exercises the fit RPC, persisted offline state, and the subsequent registered `SetModuleOnline` refusal.

### Ship-fit restrictions stopped at an old fixed list of attribute IDs

**Verdict: confirmed with the fix.** The previous fixed list omitted later `canFitShipType` attributes. Fitting now discovers `canFitShipGroup*` and `canFitShipType*` attributes from SDE names, while retaining the separate exact `fitsToShipType` rule for strategic-cruiser subsystems.

Evidence: V24.01 discovers these attributes by name in `/home/user/eve-logic/eve/common/script/dogma/baseDogmaStaticSvc.py:89-107,269-303`; EveJS reads the named group/type restrictions in `/home/user/evejs-261108.1/server/src/services/fitting/liveFittingState.js:899-953`. V24's fitting service also checks exact subsystem hull compatibility in `/home/user/eve-logic/eve/client/script/environment/fittingSvc.py:592-612`, matching EveJS `liveFittingState.js:490-514,3993-4012`. `Registered_invbroker_honors_the_twelfth_ship_fit_restriction_from_the_sde` verifies the real `FitFitting` path using the pinned SDE's `canFitShipType12` value and checks both accepted and refused hulls.

### Ship condition fractions were sent as dogma amounts

**Verdict: confirmed with the fix.** Persistence stores ship condition as fractions, but the client reads hull damage, armor damage, shield charge, and capacitor charge in their dogma units. `DogmaInfo` now multiplies each fraction by the corresponding computed capacity and uses the same resulting amounts in both `shipInfo` attributes and the ship's `shipState` row.

Evidence: V24.01's HUD subtracts hull damage from `hp` and armor damage from `armorHP`, while shield charge is already an amount, in `/home/user/eve-logic/eve/client/script/ui/inflight/shipHud/activeShipController.py:92-124`. EveJS documents and builds the same amount-valued wire attributes in `/home/user/evejs-261108.1/server/src/services/dogma/shipConditionAmounts.js:24-99`. `DogmaShipConditionHostWireTests` exercises registered `GetAllInfo`, checks all four persisted fractions as dogma amounts, and asserts that the ship-state row matches the attribute values.

### In-space modules could online below the capacitor threshold

**Verdict: confirmed with the fix.** `SetModuleOnline` previously committed the module without checking the active ship's capacitor, spending capacitor, or sending the capacitor attribute change. The registered Host path now rejects a ship below 95%, atomically spends the live ship condition to 33% of the capacitor capacity from before the module comes online, and sends the resulting charge amount change in `OnMultiEvent`. It converts that absolute amount back to a fraction of the post-online capacity for the persisted/simulated ship condition. The check is limited to the active undocked ship in the bound solar system; station, structure and no-live-simulation paths retain their existing behavior. The condition uses a revision compare-and-swap in the module transaction so a concurrent simulation-condition write cannot be overwritten by the online request.

Evidence: V24.01 calls `SetModuleOnline` from `/home/user/eve-logic/eve/client/script/dogma/clientDogmaLocation.py:698-710`; its online effect reads capacity before setting the module online, checks `const.onlineCapacitorChargeRatio`, reports the actual and required percentages, then writes that original capacity times `const.onlineCapacitorChargeRemainder` in `/home/user/eve-logic/eve/common/script/dogma/online.py:29-55`. The V24.01 constants are 95 and 33 in `/home/user/eve-logic/eve/common/lib/appConst.py:1511-1512`. In pinned SDE build 3396210, `sde.typeDogma` for Small Cap Battery I (type 1185) assigns `capacitorBonus` attribute 67 = 120 and online effect 25 applies that bonus to ship capacitor capacity attribute 482. The new end-to-end test first failed on this hull: the client amount was 42.9 GJ (33% of the pre-online 130 GJ capacity), while the server sent 82.5 GJ (33% of the post-online 250 GJ capacity). EveJS checks the 95% threshold in `/home/user/evejs-261108.1/server/src/services/dogma/dogmaService/moduleOperation.js:2612-2678` and emits its online event before refreshing the ship simulation in `moduleOperation.js:2829-2845`; its 5% remainder differs from the V24.01 client constant, so eve.rs follows the client value of 33% and records the deliberate difference here.

End-to-end coverage: `Registered_dogma_module_online_requires_95_percent_capacitor_and_persists_client_remainder` exercises the registered RPC on PostgreSQL-backed SDE data, checks the 94.9% error wire fields, a concurrent condition-write conflict, success at exactly 95%, the amount-valued notification, the 33% persisted condition, duplicate online, offline, and a capacity-increasing battery that verifies the pre-online charge amount and post-online stored fraction. Its order assertion first failed with the simulation refresh marker preceding `OnMultiEvent`; after the Host reordered the online notification before `RefreshShipAsync`, the focused test passed.

## False positives and verified behavior

### Partially trained skill after losing a prerequisite

**Verdict: false positive; server rejection is retained.** The V24 queue UI skips its local prerequisite check when a skill has any SP (`skillQueueSvc.py:206-222`), but then sends the queue to `SaveNewQueue` (`skillQueueSvc.py:142-154`). EveJS validates every queued skill's prerequisites on the server (`skillQueueRuntime.js:644-735`, called by `SaveNewQueue` at line 1454). Therefore the client-side shortcut does not establish acceptance behavior. The registered Host test `Registered_queue_rejects_a_partially_trained_skill_after_its_prerequisite_was_lost` expects the EveJS server error and verifies that neither the skill row nor queue is changed, including after restart. A permissive version was run as a negative control and failed because no refusal was returned; the restored strict behavior passed.

### Ammo prerequisite check is client-side in both references

**Verdict: false positive for the unmodified-client flow.** V24 checks charge prerequisites before the single-module and load-to-all RPCs in `/home/user/eve-logic/eve/client/script/dogma/clientDogmaLocation.py:980-997,1030-1072`. EveJS's `Handle_LoadAmmo` in `/home/user/evejs-261108.1/server/src/services/dogma/dogmaService/moduleOperation.js:3714-3795` does not repeat that skill check; it handles module ownership, charge compatibility, and inventory state. The current registered server path matches that reference behavior. No server-side RPC-fuzz or modified-client claim is made by this audit.

### Skill-point formula, stacking order, and ship simulation inputs

**Verdict: false positives; no discrepancy confirmed in the inspected paths.** The client and server skill-rate expression is primary plus half secondary; EveJS and the current queue calculator floor completion time in FILETIME ticks. Dogma's operation precedence, penalizable operations, eight-entry stacking table, and unpenalized item categories match V24.01's `/home/user/eve-logic/dogma/const.py:18-51` and `dogma/attributes/stackingNurfedAttribute.py:11-17,66-101`; the corresponding implementation is `Eve.Dogma.Operations` and `DogmaWorld`. Existing SDE-backed `DogmaServiceTests` cover stacking results for turret damage amplifiers and ballistic controls.

The Rust space simulator consumes host-resolved ship attributes through `ShipSpec` (`crates/eve-space/src/ship.rs:26-60`); its dogma adapter reads those values and current simulated health (`crates/eve-space/src/brains/dogma.rs:104-125`). It does not duplicate the .NET SDE modifier/stacking calculator. No Rust source was changed in this audit.

## Verification limits

The graphical V24.01 client was not launched. The EveJS source paths were traced, but its reference process did not have the `typeDogma` static table available, so an EveJS end-to-end RPC flow was not run. The Host tests use PostgreSQL-backed SDE data and assert build 3396210; the workspace's `.cache/sde` directory was absent.

## Local verification

Release builds with warnings treated as errors completed with 0 warnings for the touched production projects `Eve.Fitting`, `Eve.Identity`, `Eve.Skills`, `Eve.Ships`, `Eve.Services.Skills`, `Eve.Services.Ships`, `Eve.Services.Dogma`, and `Eve.Host`; the touched test projects `Eve.Fitting.Tests`, `Eve.Identity.Tests`, `Eve.Services.Dogma.Tests`, `Eve.Services.Skills.Tests`, `Eve.Ships.Tests`, and `Eve.Host.Tests` also built with 0 warnings.

Database-backed tests used `eve_p5` with PostgreSQL SDE build 3396210. Focused runs passed: fitting 66/66; entitlement 11/11; training math and character training 24/24; skill handler 56/56; ship behavior and persistence 44/44; dogma and health 22/22; space service cases 83/83; inventory capacity 1/1; selected registered Host RPC/persistence cases 12/12; and character selection 4/4. The registered capacitor Host regression was first observed failing on both the refresh/notification order and battery-adjusted capacity, then passed after the fixes. Two pre-existing Host Abyssal cases were invoked but skipped by their test-level conditions (0 passed, 0 failed, 2 skipped); skips are not counted as passing tests. Rust sources were not changed, so Rust checks were not run.
