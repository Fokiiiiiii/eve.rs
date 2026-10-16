# ST-16 handoff: station rent, observatory control towers, destruction loot, customs

Package ST-16 of the structures phase. Worktree `wt-st16` from origin/main 4828068; database `eve_p3` (`tools/dev/db-lock.sh`). Rules: `agent-rules.md`. Decisions and sources: DEVIATIONS "Structure ST-16 (2026-10-16)". Rows: TODO section 58.

## What was built

- **(3) Station office rent, due job, notices (pushed, e0a2cf2f).** The station RentOffice and the impound release fee credit the station owner (the bug was a debit with no credit). `WalletTransactions.EnsureAsync` opens a missing owner balance (keys (owner, account) 1000..1006). Bill settlement credits the resolved creditor. A station office schedules `office.rent_due`; at due time an unpaid bill impounds it (`StationOfficeImpound`, shared with the Director un-rent). Both give-up paths write rent-due and eviction notices (type 10, group Bills) through the notifications mailbox (`MailComposition.notificationMailbox`, shared gate).
- **(1) Control towers.** `ObservatoryLandmarkSource.ReadControlTowersAsync` reads group 365 structure records; `SpaceContainers` refuses a drop within 100 km of one; the 1 000 km list no longer includes towers. Test: `A_control_tower_within_100_km...`.
- **(2) Destruction loot.** `StructureDestructionLootAuthority` runs in the destruction transaction through the new `StructureTimers` callback `completeDestruction`: rigs destroyed; modules, fuel, cargo and the core move into one Abandoned Container (type 10167, marker `structure-destruction-loot:{id}`); the container is placed in space by the unanchor placement (now also for `destroyed`). Tests: `Destruction_loot_moves...`, `Destruction_loot_is_atomic...`.

## Verified (this run)

- Corporations.Tests 128/128; Structures.Tests 105/105; Wallet.Tests 40/40; Space.Tests filter SpaceContainers|MobileObservatory|Structure 75/75 (MobileObservatoryHostTests 9/9 with the new test); Host filter StructureUnanchorDropTests|StructureOfficeTests 13/13; Host filter StructureOfficeTests|StructureCompositionTests|StructureCompressionTests 33/33 (before the destruction change).
- Builds: Eve.Host.Tests, Eve.Services.Space.Tests, Eve.Structures.Tests, Eve.Corporations.Tests, Eve.Wallet.Tests all build with warnings as errors.

## Not done (in order of importance)

1. **Asset safety before loot (2b).** EveJS hands owners' items to asset safety first and loots only when that is disabled or refused. eve.rs loots everything outside corporation hangars. Needs the per-owner `WrapItemsAsync` call in the destruction callback, and the choice of which owner kinds to wrap.
2. **Customs fines and confiscation (4).** EveJS `inspectCharacterContraband` runs at undock (`undockSession`, station and structure) and at a stargate jump (`jumpSessionViaStargate`): faction rule table, smuggling skill (445), roll, fine settled by the durable `contraband.penalty-settle` job, confiscation with custody reason `contraband-confiscate`. eve.rs has only the warning (ST-3 `UndockContraband`). The wiki pages `Contraband` and `Mobile_Observatory` returned 404 to WebFetch; take the rules from the wiki once reachable.
3. **Control tower deployment (1b)** and the minimal deployable kind (sim ball, protocol, deploy RPC). Until then the 100 km rule covers only the records the store holds.
4. **Deadspace (1c).** No pocket radius in the wiki (`Deadspace` page: "a Deadspace pocket is a single grid"), the SDE or ST-11 records. Needs a source before a check is written.
5. **Station rent created before ST-16** has no creditor, so its due job does nothing. A one-time creditor migration (station owner from the station record) is needed.
6. Not tested: the Space placement of the destruction container; the Host wiring of the station branch of the due job (the Corporations test covers the helper, not the job); reading the notifications back.

## Things that will bite

- `grep`, `cat`, `find` are denied in Bash: use python3 (`python3 -I`), and do not chain `cat` after a python step that the policy might flag.
- Balances are keyed (owner_id, account_key): a single-column insert fails with 42P10. Use `WalletTransactions.EnsureAsync`.
- A failed `dotnet build` leaves the previous test DLL in place: chain `build && test` and read the build log before trusting a run.
