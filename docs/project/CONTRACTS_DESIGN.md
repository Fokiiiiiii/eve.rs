# Contracts design

## Scope and evidence

The V24.01 client has three contract types: item exchange, auction, and courier. Its client code imports the skills `Contracting`, `Advanced Contracting`, and `Corporation Contracting`; it has no `Public Contracts` skill identifier. A loan contract is not in the V24 client type flow. The first implementation slice must follow the client type and skill tables in `eve/common/script/util/contractscommon.py` and `inventorycommon/const.py`, rather than introducing another type or skill.

The startup read is established by V24 `eve/client/script/ui/shared/neocom/contracts/contracts.py:190-205`: `GetLoginInfo()` returns a KeyVal with `assignedToMe` rows (`issuerID`, `contractID`), `needsAttention` rows (`contractID`, `state`), and `inProgress` rows (`contractID`, `startStationID`, `endStationID`, `expires`). EveJS v0.12.9 `contractProxyService.js` and `contractRuntimeState.js` build these from persisted contract records. eve.rs now migrates PostgreSQL `contract_records`, `contract_items`, and `contract_bids` and wires `GetLoginInfo` and `GetMyContractEscrow` to its read model. The current slice has no contract writer, so the tables receive no gameplay records yet. Unit tests cover the V24 rowset fields; a PostgreSQL Host test covers personal/corporation scopes, expiry, accepted courier rows, and escrow sums. The service-response audit retains all remaining contract operations as open work.

Primary client references: `eve/client/script/ui/shared/neocom/contracts/contracts.py`, `contractsearch.py`, `createContractWnd.py`, and `eve/common/script/util/contractscommon.py` in eve-logic V24.01. Behaviour comparison: EveJS v0.12.9 `server/src/services/_other/contractProxyService.js` and `server/src/services/contracts/contractRuntimeState.js`.

## Persistence and ownership

Use the Host PostgreSQL store as the sole authority. A contract header stores its id, issuer character or corporation, type, availability/assignee, start and end location, create/accept/expiry/completion timestamps, lifecycle status, reward, price, collateral, duration, description, and the escrow ledger amounts. Child rows store each escrowed item snapshot and quantity; auction bids are separate ordered rows keyed by contract and bidder. Keep contract state, item transfers, wallet balance changes, wallet journal rows, and contract notifications in the same database transaction where the existing inventory and wallet providers permit it. Do not maintain a process-local duplicate contract store.

Escrowed items remain real inventory rows in the existing inventory owner/location model, with a contract escrow location or equivalent persisted ownership marker. Creation validates ownership, quantity, location, issuer authority, contract limits, item eligibility, and wallet funds before transferring any item or ISK. Failure rolls the entire operation back. Acceptance, bid replacement, deletion, expiry, and courier settlement lock the contract and affected wallet/inventory rows so retries cannot settle twice.

## Lifecycle

```text
Draft -> Outstanding -> (Accepted -> InProgress -> Finished | Failed | Rejected)
                     -> Deleted
                     -> Expired
Outstanding auction -> Outstanding with bids -> Finished after expiry or buyout
```

Item exchange settles both item and ISK legs on acceptance. Auction acceptance is a bid, not immediate settlement: a bidder escrows the current bid, an outbid bidder receives that escrow back, and expiry or buyout transfers the winning bid and items atomically. A courier acceptance escrows collateral and sets the completion deadline; completion transfers the package and reward and returns collateral, while failure transfers collateral to the issuer and returns the package according to the client and reference flow. Deletion and expiry return item/ISK escrow according to lifecycle state and record the applicable non-refundable fees. Terminal transitions are immutable and idempotent.

## Availability, skills, and limits

Availability in the V24 create flow includes public, private, my corporation, and my alliance. Private contracts target a character or corporation; corporation/alliance availability must resolve membership at the operation boundary and preserve issuer identity separately from the acting character. Corporation item escrow must use the existing corporate hangar authorization path. Public search is filtered by the client-supplied region/location and query fields; it must not expose private contract rows.

Use the client tables as the exact limit contract: personal outstanding limit is `NUM_CONTRACTS_BY_SKILL[Contracting] + NUM_CONTRACTS_BY_ADVSKILL[Advanced Contracting]`; corporation-issued limit is `NUM_CONTRACTS_BY_SKILL_CORP[Corporation Contracting]`; the V24 shared maximum is `MAX_NUM_CONTRACTS`. Enforce the same counts on the server during creation. The client source does not define a skill named `Public Contracts`; treat that user-facing term as a request for public-contract availability, not an additional skill.

## Fees, deposits, and collateral

EVE University’s [Contracts](https://wiki.eveuniversity.org/Contract) page describes public contract base value as the auction starting bid, courier reward, or the larger of reward and price for item exchange; it describes a 1% deposit (100,000 ISK minimum, 10,000,000 ISK maximum), a 0.4% broker fee (10,000 ISK minimum, 10,000,000 ISK maximum), a 0.05 percentage-point broker-fee increase per extra expiry day, a 1% sales tax (10,000 ISK minimum, 100,000,000 ISK maximum), Broker Relations reductions, and Accounting reductions. The same page distinguishes private contracts’ fixed creation fee. These contract rules are separate from market broker fee and market sales-tax calculations.

Before charging any value, implementation must compare these values and rounding/cap order against the V24 client `contractscommon.py` and EveJS contract fee helpers. Store the applied fee and deposit on the contract at creation so later skill changes cannot rewrite a settlement. A deposit is returned on successful completion; deletion or expiry forfeits it as described by the contract rules. Courier collateral is separate from the creation deposit: it is secured from the hauler at acceptance, returned on successful delivery, and paid to the issuer on failure. Do not derive collateral from item prices; it is the amount specified by the issuer.

## RPC and notification boundary

The startup response fields above are confirmed. The client callsites establish `CreateContract(contractInfo, confirm=...)`, `AcceptContract(contractID, forCorp)`, `PlaceBid(contractID, bid, forCorp)`, `CompleteContract(contractID, status)`, and `DeleteContract(contractID)`. The broader read path includes `GetContract`, `SearchContracts`, `GetMyContractEscrow`, `CollectMyPageInfo`, item-location/container reads, and bid/history reads. Each response row shape must be copied from the V24 caller and shared contract row types before implementing that RPC. The service-response audit marks several EveJS-only methods with incomplete shapes; those methods stay out of scope until a V24 caller establishes the wire contract.

Publish the V24 contract notifications only after the corresponding transaction commits. Preserve notification order from the client and EveJS flows and ensure retries do not publish duplicate state transitions. Existing audit evidence establishes startup notification consumption; exact notification payloads for each mutation still need callsite/type confirmation before wiring.

## Delivery slices

1. **Built:** PostgreSQL contract header, escrow-item, and bid tables; Host wiring for `GetLoginInfo` and `GetMyContractEscrow`; Core wire-shape tests and a PostgreSQL Host read test passed. The personal-list read remains open.
2. Item exchange create/accept/delete/expiry with inventory and wallet journaling, notifications, failure and retry coverage.
3. Public/region search, corporation issuance and item selection, availability checks, and contract-limit enforcement.
4. Auction bids, outbid refunds, buyout/expiry settlement, and bid reads.
5. Courier accept/pickup/complete/fail with collateral, deadline expiry, and notifications.
6. Headless-client walk between two characters, restart reads, and comparison with the same EveJS scenario.

A slice is complete only when its wire values, persisted state, side effects, notification order, retry behavior, and focused tests are recorded in TODO and the compatibility ledger. The graphical V24 client is a separate verification target.
