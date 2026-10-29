# CX6 handoff: clone, implant, insurance, and repair review

## Scope and current state

This pass reviewed the requested jump clone, implant, medical clone, insurance, and repair flows. Most service implementations and Host registrations already exist. The code change in this pass makes jump clone cooldown calculations use the injected game clock consistently with the bound-object timestamp.

The task is not complete end to end. Remaining known gaps include clone destinations beyond the implemented station path and pod-death home-station respawn, lifecycle/restart validation for implants, and live graphical-client repair-shop interaction. Structure repair is wired for cored Upwell structures and uses the client’s item-ID-only request shape. Insurance persistence remains outside this repair change.

## Client evidence

Whole-tree caller and consumer review in `/home/user/eve-logic`:
- `eve/client/script/ui/services/clonejump/clonejumpsvc.py:73-77` reads `kv.clones`, `kv.implants`, and `kv.timeLastJump`; `:83-92` reads ship/station state; `:216,225` submits the clone jump session change with destination, clone ID, cost, and the pod/clone flag.
- `eve/client/script/ui/shared/neocom/jumpClonesPanel.py:606,610,616,620` calls jump, destroy, rename, and install; `:491-519,626-628` computes cooldown display from last-jump time and dogma cooldown.
- `eve/client/script/ui/services/implantsBoosters/attributes.py:86-94` consumes implant attribute effects; `implantsBoostersPanel.py:17-20,29-39,101-112` consumes implant/booster notifications.
- Medical clone menu flow: `menusvc.py:3573-3575` calls `homestation/client/service.py:92-101`; `homestation/client/action.py:11-20` consumes confirmation/error behavior; `omega/client/clone_grade_svc.py:124` consumes clone grade.
- Insurance callers and consumers are documented in `docs/project/insurance-market-rpc-handoff.md`, including `base_insurance.py:113-143,172-192,298,390-401,466-504,532-542`.
- `eve/client/script/ui/services/repairshop/base_repairshop.py:61-68` requests quotes and repairs for stations/structures; `:217-238` consumes quote rows.

## Structure repair continuation (2026-10-28)

V24.01 `RepairSvc.RepairItems` sends `(itemIDs, payment)` to `RepairItemsInStation` but sends only `itemIDs` to `RepairItemsInStructure` (`eve/client/script/ui/station/repairshop/base_repairshop.py:61-68`). The repair window uses `costToRepairOneUnitOfDamage` to render item and total prices and prompts for the ISK amount (`:237-251,299-343`). Whole-client search found no other repair quote fields consumed elsewhere; the facility tax UI reads and writes `taxCorporation` through `facilitySvc.GetFacilityTaxes`/`SetFacilityTaxes` (`eve/client/script/industry/facilitySvc.py:89-94`, `eve/client/script/ui/inflight/facilityTaxWindow.py:28-29,71-72`).

EveJS 261018.1 uses the same item-ID-only structure handler (`server/src/services/station/repairService.js:207-223`; `repairRuntime.js:1470-1496`). Its `getRepairCostProfile` currently returns zero structure prices (`repairRuntime.js:426-469`), although its repair settlement separately reads the facility tax (`:396-424,1011-1040`) and credits the owner corporation (`:1282-1434`). The updated Host regression asserts a tax-inclusive quote, character debit, owner-corporation tax journal credit, restart-persisted balances and repair state, and a no-funds `NotEnoughMoney` refusal with no mutation. The owner-tax input is the existing structure `industryTaxRate`/`taxRate` setting used by Host `GetFacilityTaxes`.

The EVE University [Upwell structure service reference](https://wiki.eveuniversity.org/Structure_Browser) confirms that the repair shop is available to cored structures but does not specify its fee formula. The implementation follows the task’s requirement and reuses the existing station unit-price formula and `RepairRules` cent rounding, then applies the saved structure tax rate and credits its tax share. This intentionally differs from EveJS 261018.1’s zero-valued structure quote. The repair Host test is PostgreSQL-backed and runs on Actions; no graphical-client repair session has been performed.

## Change and checks

- `JumpCloneService` now receives `nowUnixMs` and uses it for bind-object timestamp and cooldown; `CoreServices.Create` passes the shared clock.
- Added `JumpCloneServiceTests` to verify a frozen game clock enforces cooldown and prevents store mutation.
- Updated the Host wire test and ship-install test constructors for the injected clock.

Checks actually run:
- Focused Core test `Clone_jump_cooldown_uses_the_injected_game_clock`: passed, 1/1.
- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore`: passed.
- Focused Host wire test `Jump_clone_reads_return_count_and_install_validation_labels_through_host`: passed, 1/1.
- `git diff --check`: passed.

At the time of this pass, the full Host shard, PostgreSQL-backed cases, graphical-client flow, and broader CI had not been checked. Current Host-shard results and the added PostgreSQL restart regression are recorded below. No database was used locally.

## Insurance contract restart regression (2026-10-29)

The Host insurance wire test now shuts down and rebuilds `HostComposition` after an insurance purchase, then checks that both `GetContractForShip` and `GetContracts` return the same persisted contract ID. This exercises the existing PostgreSQL-backed read/write models across a process restart rather than relying on the service instance cache.

Whole-client V24.01 references: `eve/client/script/ui/station/insurance/base_insurance.py:113-143` reads contracts and premium prices; `:145-192` selects insurable items and formats insurance data; `:198-216` submits insurance; `:298-310,327-376` renders the window from the contract/item results; `:390-401,421-428` cancels and submits insurance; `:466-504,519-563` consumes contract and quote fields in the confirmation window. The contract result fields are asserted by `InsuranceRpcHostWireTests`.

Local checks: the Host.Tests project built with 0 warnings/errors; the focused non-database station guest Host wire test passed 1/1, 0 skipped. The broad Host.Tests run without `EVE_PG_URL` reported 610 passed, 230 skipped, and 100 database-backed failures while opening connections or requiring seeded SDE rows; no PostgreSQL database was used. Actions run 40812015712 passed the insurance restart regression, and push-triggered CI run 40812006878 passed with no failed jobs. No graphical-client flow was run.

## Upwell repair billing correction (2026-10-29)

Structure repair quotes now apply the saved structure `industryTaxRate`/`taxRate` to the same station base unit price used by `RepairRules`; structure settlement charges the character and credits the owner corporation in the same transaction as item-state repair. Insufficient funds produce the client `NotEnoughMoney` error and leave wallet, journal, and repair state unchanged. Wallet and item notifications are sent after commit. `RepairStructureHostWireTests` now covers registered Host quote/charge, both journal entries, restart persistence, and insufficient-funds rollback. The pricing rule has a focused non-database test.

Whole-client evidence: `eve/client/script/ui/station/repairshop/base_repairshop.py:61-72` sends item IDs and payment for stations but only item IDs for structures; `:218-290` displays costs from `costToRepairOneUnitOfDamage`; `:300-343` asks for payment and sends the structure request. The client tree has no other consumers of the added quote fields. `eve/client/script/industry/facilitySvc.py:89-94` and `eve/client/script/ui/inflight/facilityTaxWindow.py:28-29,71-72` read/write the facility corporation tax setting. The structure setting is sourced from the existing Host `GetFacilityTaxes` data.

The EVE University Upwell structure reference documents repair-shop availability in cored structures but no HP repair fee formula. Its overheating page's free repair note concerns heat damage, a separate path. Current EveJS 261028.1 returns zero structure quote price and tax (`server/src/services/station/repairRuntime.js:426-469`); this task's station-price-plus-service-tax behavior is an intentional difference, recorded in `docs/reference/compat/DEVIATIONS.md`. V24.01 evidence for the quote, confirmation and item-ID-only structure call is `eve/client/script/ui/station/repairshop/base_repairshop.py:61-72,218-290,300-343`.

Local checks: Station service and Host.Tests builds passed with 0 warnings/errors. `Structure_repair_quote_includes_tax_and_splits_it_to_cents` passed 1/1 (0 skipped). GitHub Actions run `40845596413` on `445e515e` passed the PostgreSQL-backed registered Host regression: 1 passed, 0 failed, 0 skipped. The push-triggered `ci.yml` run was not created for this documentation-only commit. Graphical-client interaction has not been exercised.

First pushed CI on `01a26454` exposed two test regressions. The shared insufficient-wallet catch had changed the NPC-station error from its existing `CustomNotify` to `NotEnoughMoney`; the correction keeps the station error unchanged and uses the client `NotEnoughMoney` error only for structure repair. The Host test assumed a particular ship HP total instead of validating the quote's damage value; charge and refusal assertions now use that observed quote value. The final registered Host scenario passed on Actions run `40845596413`.

## Jump-clone activation settlement (2026-10-29)

Jumping now quotes and charges the same origin clone-bay fee. NPC stations charge 900,000 ISK; a structure charges its configured bay fee and the owner's corporation wallet receives it. The wallet journal records the transfer as jump-clone activation, and a body left in a structure is persisted with `location_kind = structure`. An insufficient balance rolls back the jump and returns `NotEnoughMoney` with the fee and balance.

Whole-client V24.01 evidence: `eve/client/script/ui/services/clonejumpsvc.py:193-232` fetches `lm.GetPriceForClone()`, asks the pilot to accept it, passes it to `CloneJump`, and retries after the existing replacement confirmation. `:152-165` uses the quote for clone installation; `:314-334` displays the quote in the ship-clone offer. The charsheet panel invokes the jump at `eve/client/script/ui/shared/neocom/charsheet/jumpClonesPanel.py:606-608`. The local `CloneJump.GetPriceForClone(cloneID)` at `clonejumpsvc.py:279-284` is an implant-value estimate consumed by the destroy warning at `:116-150`, not the remote bay-price RPC. Jump-cache consumers are `clonejumpsvc.py:295-312`.

EVE University, [Jump clones](https://wiki.eveuniversity.org/Jump_clones), describes the station activation fee and owner-set Upwell clone-bay fee. Current EveJS 261028.1 `server/src/services/station/jumpCloneRuntime.js:1496-1520,2425-2501` derives the fee from the origin facility and credits an Upwell owner's corporation. eve.rs matches this behavior; the former fixed-fee mismatch is recorded as resolved in `docs/reference/compat/DEVIATIONS.md`.

Local checks: Host.Tests build passed with 0 warnings/errors; the focused Core quote/charge test passed 1/1, 0 skipped; the focused non-database Host clone-read test passed 1/1, 0 skipped. No local PostgreSQL database was used. The registered settlement scenario is PostgreSQL-backed. Actions run `40864668889` found that the station journal assertion expected a reference ID, while station wallet adjustment correctly records null; the test expectation is corrected. Run `40866680859` then found that multiple fixture characters reused the same name; the fixture now generates unique names. Runs `40868797250` and `40870324669` showed the structure fixture needed both a registered default profile and the character's authoritative corporation-membership relation for docking access; both are now provisioned. The graphical V24 client flow has not been exercised.
