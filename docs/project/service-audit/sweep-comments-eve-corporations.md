# Eve.Corporations comment and dead-code sweep

Date: 2026-11-04

## Scope and changes

Reviewed comments and private-member usage in `dotnet/src/Eve.Corporations` and its tests. Corrected the summaries for shareholding and rent-bill records, clarified station-bill creditor behavior and wallet-division access, replaced the item-lock store summary with its vote/read responsibility, and corrected the station-bill test class summary. Removed three file banners that repeated historical EVE.js provenance and an old source hash. The historical `PROVENANCE.md` remains unchanged.

The station-ID seeded 10k–110k NPC office pricing described by the earlier comment sweep has since been removed. The pinned SDE has no office rental price field; the implementation now uses EveJS 261108.1’s static 10,000 ISK NPC-station rental value consistently for quote, rent settlement, impound release, and stationSvc.GetStation. See `docs/project/deepfix-corporations.md` for the source comparison and registered Host regression.

The slice had no stale issue/status markers or commented-out code. CBM call/usage checks found no private member safe to remove; no helper or member was deleted.

## Client and reference evidence

- V24.01 `itemLocking.py:25-38` binds the `itemLocking` remote service and reads locked items by location and locked-item locations. `corp_ui_accounts.py:527,601-611` consumes those results; `corp_ui_votes.py:174-190,297,738` exposes lock/unlock votes, reads locked items, and submits the vote; `item.py:337-349` and `corp_ui_accounts.py:500-503` consume lock-change UI events.
- Current EveJS 261028.1 `server/src/services/corporation/itemLockingService.js:36-61` returns item records with `itemID`, `typeID`, `ownerID`, and `locationID`, plus the set of locations with readable locked items. Its C# counterpart and wire shapes were not changed.
- V24.01 `transactionsPanel.py:210-224` shows the selected wallet division to members and all divisions to accountants and junior accountants. Current EveJS `server/src/services/corporation/corporationAuthority.js:130-143` applies the division Account Take role for other members, matching `CanReadWalletDivisionAsync`. This sweep changed its description only.

## Verification

- `Eve.Corporations.csproj`: build succeeded, 0 warnings and 0 errors.
- `Eve.Corporations.Tests.csproj`: build succeeded, 0 warnings and 0 errors.
- `StationBillCreditorMigrationTests`: 1 passed, 0 skipped.
- Full `Eve.Corporations.Tests` run on `eve_p10`: 180 passed, 0 skipped. The first run exposed that the new database lacked the pinned SDE; after loading SDE build 3396210 and the pinned mission data with `eve-sde ensure`, the focused and full runs passed.
- The final builds and test runs were repeated after fast-forwarding `origin/main`.

## Actions handoff

Implementation commit `483d2d0b` is on `main`. Selected Agent Test run 1308 (`41842510361`) passed with no failed tests. Push-triggered main CI run 1504 (`41842432716`) succeeded with no failed tests or jobs.
