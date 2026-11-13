# raffleProxy implementation record

## Baseline and scope

Before this change, Host composition already created and migrated `RaffleProxyStore` when `EVE_PG_URL` was configured, registered its durable expiry handler, and supplied `InvokeAsync` to the `raffleProxy` RPC callback (`HostComposition.cs:124-128,167-170,457`; `GameRpcServices.cs:53-54,82-102`). The coverage file had 18 call-shape rows, including the optional `size` and `running_id` forms (`service-audit/EVE_RS_COVERAGE.csv:1534-1551`). The store already persisted raffle rows and tickets and used the shared item, wallet, and scheduler services.

This change completes the observable Host path: committed item and wallet effects, raffle notifications, live-session subscriptions, Host clock wiring, and the global HyperNet kill switch. No parallel inventory, wallet, raffle, or timer store was added.

## V24.01 client contract

A whole-tree search of `/home/user/eve-logic` found the sole direct proxy accessor in `raffles/client/raffle_service.py:28-30`; the public methods below delegate through that accessor. The creation payload fields come from `raffles/common/raffle_data.py:8-21`. The whole-tree caller trace also covers window open/close subscriptions (`raffles/client/controller.py:18-28`), character statistics (`raffles/client/character_stats.py:37`), details reads and reward delivery (`raffles/client/details/controller.py:17-23,59-65`), browse (`raffles/client/browse/controller.py:42-44`), history (`raffles/client/history/controller.py:34-35`), active tickets (`raffles/client/storage.py:33-41`), ticket actions and per-raffle subscriptions (`raffles/client/raffle.py:92-115`), create and type statistics (`raffles/client/create/controller.py:142-165`), and the availability/UI entry points (`raffles/client/raffle_service.py:32-60`; `eve/client/script/ui/eveCommands.py:744,2025`).

| Call shape | Client arguments | Expected result |
|---|---|---|
| `AmIBanned()` | none | boolean |
| `SubscribeToTickets()`, `UnSubscribeFromTickets()` | none | `None` |
| `SubscribeToRaffle(raffle_id)`, `UnsubscribeFromRaffle(raffle_id)` | raffle ID | `None` |
| `CreateRaffle(creation_data)` | keyval with `owner_id`, `location_id`, `solar_system_id`, `token_id`, `token_location_id`, `item_id`, `type_id`, `ticket_count`, `ticket_price`, `restriction_id` | raffle ID |
| `GetCreatedParticipated()` | none | keyval with `raffles_participated`, `raffles_won`, `finished_delivered`, `finished_undelivered`, `finished_expired`, `created_running` |
| `GetActiveHistoricPrices(type_id)` | type ID | keyval with historic and active count/min/max/average values |
| `AwardItem(raffle_id)` | raffle ID | `None` |
| `Grab()` | none | list of raffle keyvals |
| `FilteredGrab(filters, constraints)` | keyword `filters`, `constraints` | list of raffle keyvals |
| `FilteredGrab(filters, constraints={}, size)` | keyword `filters`, empty `constraints`, and `size` | size-limited list of raffle keyvals |
| `GetMyRaffleHistory()` | none | `(raffle keyval list, page_size)` tuple |
| `GetMyRaffleHistory(running_id=cursor)` | keyword `running_id` | next history page tuple |
| `GetMyActiveTickets()` | none | list of ticket keyvals |
| `GetRaffle(raffle_id)` | raffle ID | raffle keyval |
| `BuyTicket(raffle_id, ticket_number)`, `BuyRandomTickets(raffle_id, ticket_count)` | raffle ID and ticket number/count | updated raffle keyval |

The call definitions and client consumers are in `raffle_service.py:78-104,123-175`. History requests 50 rows and continues while the page is full, using the smallest returned `running_id` as the next cursor (`raffle_service.py:142-150`). The client raffle storage consumes the create, update, ticket, and finish notifications (`raffles/client/storage.py:11-15,45-67`).

The server also emits inventory and wallet effects. V24.01 inventory code consumes `OnItemChange(item, change, location)` (`eve/client/script/environment/invCache.py:229-304`, `eve/client/script/environment/godma.py:1208-1217`). The raffle wallet notice uses the existing account-change path. The client checks `HyperNetKillSwitch` while deciding availability (`raffle_service.py:48-60`) and refreshes that decision when global config changes (`raffle_service.py:182-190`).

## EveJS v0.12.9 comparison

`server/src/services/raffles/raffleProxyService.js:20-145` defines the same proxy methods and forwards them to the raffle runtime. Each handler checks availability; the shared check returns the wrapped `raffles.RafflesError("HyperNetDisabled")` when `hyperNetKillSwitch` is set (`raffleAvailability.js:10-24`). The runtime creates raffle values with the same 18 fields and ticket values with `running_id`, `raffle_id`, `owner_id`, and `number` (`raffleMarshal.js:14-30`).

EveJS sends inventory changes before `OnRaffleCreatedServer` on creation (`raffleRuntime.js:680-699`); on purchase it sends tickets, raffle update, then finish on sellout (`raffleRuntime.js:1303-1328`; `raffleNotifications.js:9-40`). Award delivery notifies the winner and previous owner of the inventory change, then updates the raffle (`raffleRuntime.js:780-817`). Expiry refunds ticket owners, restores the item, then sends raffle update and finish (`raffleRuntime.js:1131-1164`). Interested sessions include the caller, raffle/ticket subscribers, the raffle owner, and ticket owners (`raffleSubscriptions.js:62-111`).

Creation validation also uses EveJS's active-ship exclusion (`raffleRuntime.js:523-550`; `raffleValidation.js:73-78`) and its wrapped `raffles.CreateError(reason)` contract (`common/machoErrors.js:135-142`). Ticket purchase, not-found and delivery failures use the corresponding wrapped raffle exception classes (`common/machoErrors.js:144-153`). The client consumes creation errors as `RafflesError.msg` (`raffles/client/create/controller.py:107-119,143-150`), so eve.rs returns those exception classes and messages instead of a generic `UserError`.

The EveJS package exposes server-start and migration scripts but no raffle test command (`server/package.json:6-17`); this task compared its runtime and notification source, but did not execute a separate EveJS raffle scenario. The intentional behavior difference is `AmIBanned`: EveJS always returns false (`raffleProxyService.js:29-33`), while eve.rs reads the persisted account ban value from `identity.accounts`. The V24 caller expects a boolean and uses it to deny access (`raffle_service.py:32-59`); Host tests cover unbanned, banned, and no-account sessions. Mutating calls require a known, unbanned account.

## eve.rs state, effects, and notifications

- Host composition injects the game clock, current character sessions, a wallet announcer, and a live provider for `HyperNetKillSwitch` (`HostComposition.cs:96,124-128,182-187,527-550`; `HostRuntime.cs:39-47,93-97`). The kill-switch provider reads the effective `GlobalConfigStore.Current()` value on each call. Disabled calls return the EveJS wrapped class, argument, and `msg` state; the registered Host wire test asserts that shape.
- `raffle_records`, `raffle_tickets`, and `raffle_bulk_operations` are the persisted raffle state. Create, ticket purchase, sellout payout, award, and expiry use the existing `PgItemStore` and `WalletTransactions` in PostgreSQL transactions. Expiry is scheduled through `DurableScheduler`; startup reconciles existing running raffles (`RaffleProxyStore.cs:43-73,75-147,153-269,271-400,870-1029`).
- Create checks the active `shipid`, live hangar custody, station/system, type lists, singleton rules, ticket limits, price and token amount. Failures return `raffles.CreateError(reason)` with the client's expected `msg`. A successful transaction commits escrow and token consumption before sending two `OnItemChange` packets. `OnRaffleCreatedServer(clientID, raffle_id, raffle)` is deferred until after the RPC response, matching the client's `self.raffle_id` assignment followed by its created callback (`create/controller.py:100-105,142-150`; `RaffleProxyStore.cs:298-439,554-588`). Prize stack quantity is preserved: the V24 client singleton check reads only the item's `singleton` flag (`menucheckers/baseCheckers.py:512-514`), and EveJS escrow changes the flag without splitting/removing the stack (`raffleInventory.js:295-322`). Rows come from the committed item plus pinned SDE group/category values.
- A new ticket purchase commits buyer debit, ticket ownership, sold count, and possible seller payout together. After commit, wallet balance notices precede `OnTicketsUpdatedServer(charid, {raffle_id: sold_count})`, `OnRaffleUpdatedServer(clientID, raffle_id, raffle)`, and optional `OnRaffleFinishedServer(charid, raffle_id, winning_ticket)` (`RaffleProxyStore.cs:590-613,957-1028,1040-1126`). Bulk retry keys prevent a duplicate debit or duplicate raffle notification.
- `AwardItem` verifies that the caller owns the winning ticket, moves the escrow item to the winner's hangar in the same transaction as the status change, then notifies the winner and previous owner of the item change and interested sessions of the raffle update (`RaffleProxyStore.cs:255-296,615-622`).
- Expiry jobs carry a raffle ID and settle that exact due row in the scheduler's transaction; they do not reuse the 100-row opportunistic sweep. The operation refunds each ticket owner, restores escrow to the creator, and marks the raffle expired atomically. Post-commit order is wallet changes, creator item change, raffle update, then finish with `None` as the winning ticket (`RaffleProxyStore.cs:47-60,165-250`).
- Private browse rows are visible to the creator or a character who owns a ticket; other viewers receive no row (`raffleHelpers.js:358-384`; `RaffleProxyStore.cs:893-935`). A zero-ticket random purchase returns the current raffle only while it is still running; completed or expired raffles return the wrapped `raffles.TicketUnavailableError`, after the running-row check (`raffleRuntime.js:722-750`; `RaffleProxyStore.cs:957-974`).
- Subscriptions live for the session lifetime and are cleared on close; notifications also reach the current caller and active creator/ticket-owner sessions (`RaffleProxyStore.cs:631-650` and the recipient resolution helpers).

## Verification record

The database-backed tests use `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p8`. The initial Host wire regression for the kill switch failed before its implementation: `AmIBanned` returned a successful call response instead of the expected wrapped disabled error. After wiring the provider, that test passed. The Host composition test also passed with explicit assertions for both an unknown-account and a banned-account result.

The focused raffle slice passed 7 tests with 0 skipped using the `Raffle` filter in Release. It covers `RaffleCompositionTests`, registered TCP dispatch in `HostServerTests`, persistence/expiry (including stack quantity preservation), global price calculations, and station validation. `HostServerTests` then passed 482 cases with 0 skipped, including the registered TCP raffle flow. Release builds of `Eve.Host` and `Eve.Host.Tests` passed with `-warnaserror` and 0 warnings. The graphical V24.01 client was not run. EveJS was source-traced but not executed end to end.

TDD regressions reproduced and fixed during this work: the created notification arrived before the create RPC response; ticket holders could not browse their private raffle; an expiry job settled only the first raffle in a 100-row sweep; an item matching the active ship was escrowed; a zero-count purchase succeeded after sellout; and valid multi-unit prizes were rejected. The related tests now assert response/notification order, private visibility, targeted expiry at row 101, unchanged custody on active-ship refusal, a wrapped ticket-unavailable error for a completed raffle, and escrow quantity retention for a stackable prize.
