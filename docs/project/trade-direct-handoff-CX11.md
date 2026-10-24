# Direct trade handoff (CX11)

Updated: 2026-10-24

## Implementation

- `Eve.Services.Station/TradeMgrService.cs` parses `Add(itemID, sourceLocationID, qty?)` and `MultiAdd(itemIDs, sourceLocationID)`, maps client-visible refusals, and emits the offer, priming, money, completion and cancel notifications in client-consumed order.
- `Eve.Trade/TradeAuthority.cs` validates active-ship, Asset Safety type 60, SDE restricted types 36/142, ownership and source-location ancestry. A full MultiAdd batch validates before mutation. A container and its descendants transfer together. Existing transactional custody, abort and recovery remain in place.
- `ItemTypeCatalog` reads tradability from the pinned SDE; `Items.TreeAsync` locks and returns the transfer subtree. Host composition injects the catalog.
- Host dispatch/persistence coverage exercises active ship, Asset Safety, restricted type, wrong source, foreign owner, invalid mixed batch rollback, successful ISK/item settlement including a descendant, abort, restart recovery and concurrent completion. Trade authority coverage exercises atomic preflight and accessible nested sources.
- Updated the focused trade headless walk to send the V24 source location argument.

## Client evidence

- `/home/user/eve-logic/eve/client/script/environment/invControllers.py:1667-1691`: active ship, Asset Safety and `is_tradable` checks; client item Add behavior.
- `invControllers.py:212,225,489-565` and `invCache.py:1019,1057`: Add/MultiAdd and forwarded source/quantity arguments.
- `eve/client/script/ui/station/pvptradesvc.py:41-50,69-110`: initiation and trade notification consumers.
- `eve/client/script/ui/station/pvptradewnd.py:96-109,119-139,159-167`: manifest construction, offer truth handling, money offer and completion. `invCache.py:219,229` consumes item changes.
- Client restriction types are 36 and 142 (`inventoryrestrictions/common/checks.py:18-19`, `evetypes/const.py:14,21`); Asset Safety is type 60 (`inventorycommon/const.py:1329`).

## Executable reference

EveJS v0.12.9 source-chain and item eligibility checks are `server/src/services/trade/tradeMgrService.js:1200-1335`; Add/MultiAdd are `:2019-2073`. Offer reset is `:817-829`, abort is `:1068-1168,2189-2208`, OfferMoney/MakeOffer are `:2075-2187`. eve.rs matches the intended client behavior while making batch staging atomic and transferring descendants in the completion transaction.

## Prior handoffs

The task-mentioned `jobs/461-trade-item-eligibility.txt`, `462-trade-container-descendants.txt`, `463-trade-multiadd-preflight.txt` and `464-trade-nested-inventory-sources.txt` were not present in this worktree; no `jobs/` directory was found. The prior report `/home/user/reports/audit-market-wallet-trade-inv.md` was inspected. Their stated topics are all incorporated above.

## Verification

- Earlier local builds of `Eve.Host.Tests` and `Eve.Trade.Tests` succeeded with .NET 10, `UseSharedCompilation=false`, and node reuse disabled. No PostgreSQL tests were run locally. A later Space.Tests build and Host.Tests recompile were canceled after prolonged shared-machine resource starvation.
- Focused Actions run `40073577535` passed on `859cf2c4b67c`: Host 3/3, Trade 2/2 and `test_walk_trade.py` 1/1; no skips.
- The first push gate `40073571150` exposed outdated trade/warp expectations and a structure walk that needed session-transition waits. The later focused run `40080249666` passed Host 3/3 and Trade 3/3; Space wire test was 0/1 and live cases 22/23. Its live failure was the structure walk; the fast run had 0 skipped.
- Push gate `40080240344` completed with Host account-wallet and Space wire test failures; all other reported jobs, including end-to-end server stack and Windows, succeeded. The account fixture now funds the corporation registration fee; the wire fixture supplies its ship type to DogmaStatic. The structure walk now waits before undock, checks that undock succeeded, and waits for the state notifications.
- Focused Actions run `40085281024` on `f9d453beafe6` passed: Host 4/4, Trade 3/3, Space 1/1, and live trade/structure 23/23; no tests were skipped. This includes the account-wallet corporate notification expectation correction.
- Push gate `40085268617` on `f9d453beafe6` failed in `test_a_director_anchors_an_astrahus_from_its_ship_in_a_legal_system_and_the_structure_is_placed_and_told`: the warp call answered, but Anchor then reported `You must be in space to deploy a structure.` Host, other .NET shards, Windows, Rust, Python and fixtures passed; Space shard 2/2 passed with a separate BeltRatsHost test failure.
- Commit `2caaf673` waits for the client session to clear `stationid` before warping. Focused run `40087938567` on `88800811` passed Host 4/4, Trade 3/3, Space 1/1 and live 23/23 with no skips, but that push gate still had an E2E failure. The following focused run `40088879191` showed the client does not receive a ship slim in the structure lookup stream, so that assertion was removed. The test now sends the ballpark `UpdateStateRequest` and waits for its response before warping, following the existing undock walk. Verification is pending.
- Earlier push gate `40083548997` had end-to-end and Host shard failures. The Host failure was the corporate notification expectation and is corrected; its Space shard was canceled after 30 minutes, which is not a pass.
- Earlier broad fast CI reported eight slow live skips and one training-speed skip; those skips are not passes. Graphical-client execution was not performed.
