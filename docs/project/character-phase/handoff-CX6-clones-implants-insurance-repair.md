# CX6 handoff: clone, implant, insurance, and repair review

## Scope and current state

This pass reviewed the requested jump clone, implant, medical clone, insurance, and repair flows. Most service implementations and Host registrations already exist. The code change in this pass makes jump clone cooldown calculations use the injected game clock consistently with the bound-object timestamp.

The task is not complete end to end. Remaining known gaps include clone destinations beyond the implemented station path and pod-death home-station respawn, lifecycle/restart validation for implants, and live graphical-client repair-shop interaction. Structure repair is now wired for cored Upwell structures and uses the client’s free-repair request shape. Its PostgreSQL-backed Host regression is assigned to Actions. Insurance persistence remains outside this repair change.

## Client evidence

Whole-tree caller and consumer review in `/home/user/eve-logic`:
- `eve/client/script/ui/services/clonejump/clonejumpsvc.py:73-77` reads `kv.clones`, `kv.implants`, and `kv.timeLastJump`; `:83-92` reads ship/station state; `:216,225` submits the clone jump session change with destination, clone ID, cost, and the pod/clone flag.
- `eve/client/script/ui/shared/neocom/jumpClonesPanel.py:606,610,616,620` calls jump, destroy, rename, and install; `:491-519,626-628` computes cooldown display from last-jump time and dogma cooldown.
- `eve/client/script/ui/services/implantsBoosters/attributes.py:86-94` consumes implant attribute effects; `implantsBoostersPanel.py:17-20,29-39,101-112` consumes implant/booster notifications.
- Medical clone menu flow: `menusvc.py:3573-3575` calls `homestation/client/service.py:92-101`; `homestation/client/action.py:11-20` consumes confirmation/error behavior; `omega/client/clone_grade_svc.py:124` consumes clone grade.
- Insurance callers and consumers are documented in `docs/project/insurance-market-rpc-handoff.md`, including `base_insurance.py:113-143,172-192,298,390-401,466-504,532-542`.
- `eve/client/script/ui/services/repairshop/base_repairshop.py:61-68` requests quotes and repairs for stations/structures; `:217-238` consumes quote rows.

## Structure repair continuation (2026-10-28)

V24.01 `RepairSvc.RepairItems` sends `(itemIDs, payment)` to `RepairItemsInStation` but sends only `itemIDs` to `RepairItemsInStructure` (`eve/client/script/ui/station/repairshop/base_repairshop.py:45-68,331`). The repair window uses `costToRepairOneUnitOfDamage` to render item and total prices (`:237-251,299-303`). Whole-client search found no other callers or consumer of `structureTaxRate` and `baseCostToRepairOneUnitOfDamage`. Since no amount reaches the structure service, the Host reports zero structure cost/tax and does not debit a wallet.

EveJS v0.12.9 uses the same no-payment structure handler (`server/src/services/station/repairService.js:207-223`; `repairRuntime.js:1470-1496`) and sets structure quote unit cost and tax to zero (`repairRuntime.js:426-469`). The registered Host regression covers the quote, full persisted repair, unchanged wallet and item notification (`dotnet/tests/Eve.Host.Tests/RepairStructureHostWireTests.cs`). Locally, `Eve.Services.Station` and `Eve.Host` built with 0 warnings/errors, and `RepairRulesTests.A_zero_cost_target_is_repaired_without_a_wallet_charge` passed 1/1 with 0 skipped. The PostgreSQL-backed Host scenario is assigned to Actions. No graphical-client repair session has been performed.

## Change and checks

- `JumpCloneService` now receives `nowUnixMs` and uses it for bind-object timestamp and cooldown; `CoreServices.Create` passes the shared clock.
- Added `JumpCloneServiceTests` to verify a frozen game clock enforces cooldown and prevents store mutation.
- Updated the Host wire test and ship-install test constructors for the injected clock.

Checks actually run:
- Focused Core test `Clone_jump_cooldown_uses_the_injected_game_clock`: passed, 1/1.
- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore`: passed.
- Focused Host wire test `Jump_clone_reads_return_count_and_install_validation_labels_through_host`: passed, 1/1.
- `git diff --check`: passed.

Not checked: full Host test shard, PostgreSQL-backed tests, graphical-client flow, or broader CI. No database was used. Do not treat this handoff as completion of all requested flows.
