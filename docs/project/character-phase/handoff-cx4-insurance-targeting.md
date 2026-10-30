# cx4 insurance and target-notification handoff

## Change

- CONCORD ship losses now pay the 40% base amount, while the active contract does not contribute its insured fraction. The Host insurance service accepts the loss for settlement rather than returning `NotEligible`.
- Ship destruction now emits `OnTarget("otherlost", targetID, "Exploding")` to lock holders. Departure, jump, docking, and abyssal transitions retain the ordinary reasonless loss.
- Instant target locks return `pending=true` and complete through the `OnTarget("add", targetID)` notification.

## Evidence from V24.01 client source

- `eve/client/script/parklife/targetMgr.py:585-598` dispatches `OnTargets` events and forwards the reason to loss handlers. At `:1130-1144`, `OnTargetLost` calls `ClearDeadTarget` for `Exploding`, and `ClearTarget` for other loss reasons; `:1145-1164` defines the dead-target cleanup.
- `nodegraph/client/events/target.py:8-21` treats `OnTargets` tuples with event `lost` and reason `Exploding` as `TargetExploded`.
- `eve/client/script/parklife/targetMgr.py:1363-1381` calls `OnTargetAdded` itself only when `_LockTarget` receives a false pending flag. For instant locks, the server also emits `OnTarget("add", ...)`, so `pending=true` prevents duplicate completion.
- Whole-source search for `Exploding` found the `targetMgr.py` and nodegraph reason consumers above; other matches concern unrelated ship effects and mission or dogma logic. The sim now matches the consumed target-event shape.

## Insurance reference checks

- EVE University Insurance lists repackaging, transfer/sale, contract, corporation hangar placement, and CONCORD highsec destruction as coverage voiding conditions. EveJS 261028.1 `server/src/services/insurance/insuranceRuntime.js:1466-1530` checks singleton changes (repackaging), owner changes (transfer/sale), and corp-hangar placement; `:1532-1554` handles explicit repackaging. eve.rs has `Repackaged`, `OwnerChanged`, and `PlacedInCorporationHangar` mutation reasons in `dotnet/src/Eve.Services.Core/InsuranceService.cs:148-165`; `InsuranceServiceTests` has cases for each at `:488-490`. The corporation-hangar reason is the page's corporation condition, while trades, market sales, and contracts are covered by owner changes in this implementation.
- EveJS `:1274-1464` voids and skips payment for CONCORD; eve.rs deliberately follows EVE University on this payout case and pays the base 40%. The compatibility record is in `docs/reference/compat/DEVIATIONS.md`.

## Checks and remaining work

After merging `origin/main`, local checks passed: Core build; Host test-project build; all 34 `InsuranceServiceTests`; both selected Host insurance tests; Rust `eve-space` check, format, workspace clippy, all 39 targeting tests and all 28 death tests. Before the merge, the unrelated dirty `SpaceDrones.cs` made the Host test project fail against its clean `WireDrones` fixture; that source was preserved in `stash@{0}`, and the clean post-merge Host build passed without a fixture change.

No PostgreSQL was used, as required by the task header. The graphical client was not run. Before pushing, merge `origin/main`, repeat relevant checks, commit only this task's files, and push to `main`. Keep this note current after the push and record the Actions result.
