# ST-16 handoff: station rent, observatory deadspace and control towers, destruction loot, customs

Package ST-16 of the structures phase. Worktree `wt-st16` from origin/main 4828068; database `eve_p3` (`tools/dev/db-lock.sh`). Rules: `agent-rules.md`. Decisions and sources: DEVIATIONS "Structure ST-16 (2026-10-16)". Rows: TODO section 58.

## What was built (each increment pushed to main)

- **(3) Station office rent (e0a2cf2f).** The station RentOffice and the impound release fee credit the station owner (the bug was a debit with no credit). `WalletTransactions.EnsureAsync` opens a missing owner balance (keys (owner, account) 1000..1006). Bill settlement credits the resolved creditor. A station office schedules `office.rent_due`; at due time an unpaid bill impounds it (`StationOfficeImpound`, shared with the Director un-rent). Both give-up paths write rent-due and eviction notices (type 10, group Bills) through the notifications mailbox (`MailComposition.notificationMailbox`, the shared gate).
- **(1) Mobile Observatory, control towers (3b5d0bce).** `ObservatoryLandmarkSource.ReadControlTowersAsync` reads SDE group 365 structure records; `SpaceContainers` refuses a drop within 100 km of one; the 1 000 km landmark list excludes towers. Test: `A_control_tower_within_100_km...`.
- **(1) Mobile Observatory, deadspace (A; pushed).** `SpaceContainers.InDeadspaceAsync`: refused within 1e9 m of an ST-11 cosmic site position (`ReadDeadspaceAnchors` from `CosmicSiteDirector.ActiveAsync`). The radius is EveJS `DEADSPACE_SITE_RADIUS_METERS` (`services/dungeon/deadspaceWarpPolicy.js`). Test: `An_observatory_is_not_anchored_in_a_deadspace_pocket...`.
- **(2) Destruction loot, asset safety first (B; pushed).** `StructureDestructionLootAuthority` runs in the destruction transaction (the `StructureTimers` callback `completeDestruction`): rigs destroyed; each owner's items are offered to the existing wrap (`PersistenceStructureAssetSafetyReadModel.WrapDestroyedOwnerAsync`, wired through a late-bound lambda in `CharacterServicesComposition`); what stays goes to one Abandoned Container (type 10167) at the structure's position, with the Quantum Core; the container is placed in space by the unanchor placement. FLEX, wormhole and Pochven systems are not wrapped (EveJS asset safety disabled). Tests: `Destruction_wraps_each_owner_first...`, `Destruction_loot_moves...`, `Destruction_loot_is_atomic...`.
- **(4) Customs fine and confiscation (C; pushed).** `UndockContraband.ApplyAsync`: fine = quantity x SDE base price x SDE `fineByValue` for the matching faction rule at a system whose security reaches `confiscateMinSec`; charged to the pilot's wallet up to its balance (`WalletTransactions.AdjustAsync`, reason "Contraband fine", counterparty 0 = burnt); the matching cargo items are deleted; the client is told each removal (`OnItemChange`, junk location). Hooks: `SpaceSessions.UndockFromAsync` (after the warning check) and `SpaceSessions.JumpAsync` (after the simulation accepts, in the destination system). Test: `Penalty_charges_the_fine_to_the_wallet_and_removes_the_matching_contraband_once`.

## Verified

- Narrow runs (eve_p3): Corporations 128/128; Structures 105/105; Wallet 40/40; Space `SpaceContainers|MobileObservatory|Structure` 75/75; MobileObservatory after A 12/12; Host `StructureUnanchorDropTests` 4/4; Host `StructureOfficeTests|StructureCompositionTests|StructureCompressionTests` 33/33; Space `UndockContraband|StructureUndocking|SpaceServiceTests` 137/137; Space `Jump|Gate|Stargate` 82/82.
- Wide runs before A, B and C (items 1 and 2 state): Host full 366/366, Space full 907/907, zero failures and zero skips.
- Wide run after A, B and C: see the report (run once more, full Host and full Space).

## Not done (named precisely)

1. **Inspection roll and smuggling chance (C).** EveJS `resolveCharacterSmugglingChance` reads attribute 445 from the pilot's active implants and skills; eve.rs applies no inspection roll, so the contraband is always detected. The data source is the EveJS dogma modifier path (`getActiveImplantCharacterModifierEntries`); eve.rs's pilot-level skill and implant modifier read for attribute 445 was not located in this package.
2. **Standing loss (C).** EveJS `applyStandingChanges` (faction standing, `standingLoss` from the SDE). eve.rs has no standing-change hook (no `AdjustStanding` or `ApplyStanding`, no `StandingService` in the sources): needs the standing system first.
3. **Durable penalty job (C).** EveJS settles the penalty through `contraband.penalty-settle` with a deferred retry; eve.rs settles in the inspection transaction.
4. **Ref type of the fine (C).** Journal ref type 0; the static data names none, EveJS's penalty path names none in the code read.
5. **Real asset-safety wrap in the destruction (B).** The Host test uses a wrap fake; the composition wiring to the real read model is not covered by a test.
6. **Mission pockets (A).** The `Eve.Dungeons` mission pockets are not positioned in the simulation, so the observatory deadspace check covers the ST-11 cosmic sites only.
7. **Control tower deployment (1b).** eve.rs cannot place a control tower; the rule applies to the group 365 records the store holds. The minimal deployable kind (sim ball, protocol, deploy RPC) is not added.
8. **Station rent created before ST-16** has no creditor, so its due job does nothing. A one-time creditor migration is needed.
9. **Client presentation.** The customs removal notice, the fine's wallet line and the destruction container are not checked on the graphical client.
10. **Wiki pages.** `Contraband` and `Mobile_Observatory` returned 404 to WebFetch; the mechanics follow EveJS and the SDE numbers.

## Things that will bite

- `grep`, `cat`, `find` are denied in Bash: use python3 (`python3 -I`).
- Balances are keyed (owner_id, account_key): a single-column insert fails with 42P10. Use `WalletTransactions.EnsureAsync`.
- A failed `dotnet build` leaves the previous test DLL in place: chain `build && test` and read the build log before trusting a run.
- Upstream merges touch TODO.md and DEVIATIONS.md at the end: keep both sections.
