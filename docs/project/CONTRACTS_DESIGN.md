# Contracts design

## Scope and evidence

The V24.01 client has three contract types: item exchange, auction, and courier. Its client code imports the skills `Contracting`, `Advanced Contracting`, and `Corporation Contracting`; it has no `Public Contracts` skill identifier. A loan contract is not in the V24 client type flow. The first implementation slice must follow the client type and skill tables in `eve/common/script/util/contractscommon.py` and `inventorycommon/const.py`, rather than introducing another type or skill.

The startup read is established by V24 `eve/client/script/ui/shared/neocom/contracts/contracts.py:190-205`: `GetLoginInfo()` returns a KeyVal with `assignedToMe` rows (`issuerID`, `contractID`), `needsAttention` rows (`contractID`, `state`), and `inProgress` rows (`contractID`, `startStationID`, `endStationID`, `expires`). EveJS v0.12.9 `contractProxyService.js` and `contractRuntimeState.js` build these from persisted contract records. eve.rs migrates PostgreSQL `contract_records`, `contract_items`, `contract_requested_items`, and `contract_bids`, and wires `GetLoginInfo` and `GetMyContractEscrow` to its read model. The `CreateContract` RPC validates client terms, availability, station context, corp contract-manager role, skills, and fees before calling the Host writer. The writer stages offered inventory, requested-item terms, and issuer reward escrow, broker fee, sales tax, and deposit with wallet journal entries in one PostgreSQL transaction. An advisory transaction lock serializes outstanding-limit checks per issuer; the PostgreSQL Host tests cover persisted escrow, partial stacks, limit rejection, and rollback on invalid items or insufficient wallet funds. Acceptance, settlement, and subsequent lifecycle transitions remain open.

Primary client references: `eve/client/script/ui/shared/neocom/contracts/contracts.py`, `contractsearch.py`, `createContractWnd.py`, and `eve/common/script/util/contractscommon.py` in eve-logic V24.01. The item-exchange writer enforces the UI expiry choices (1, 3, 7, 14, or 28 days), 50-character title and 4000-character description limits, and the 500-item selection cap from `EXPIRE_TIMES`, `MAX_TITLE_LENGTH`, and `MAX_NUM_ITEMS`. Behaviour comparison: EveJS v0.12.9 `server/src/services/_other/contractProxyService.js` and `server/src/services/contracts/contractRuntimeState.js`.

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

Use the V24 client tables as the exact capacity contract: personal outstanding limit is the direct `NUM_CONTRACTS_BY_SKILL[Contracting] + NUM_CONTRACTS_BY_ADVSKILL[Advanced Contracting]` table sum; corporation-issued capacity uses `NUM_CONTRACTS_BY_SKILL_CORP[Corporation Contracting]`; and the shared corporation-assigned maximum uses `MAX_NUM_CONTRACTS`. `ContractCreationLimits` copies these six-entry tables from `eve/common/script/util/contractscommon.py`; it does not extrapolate a per-level increment. The Host checks the applicable limit inside a per-issuer advisory-locked transaction. The client source does not define a skill named `Public Contracts`; treat that user-facing term as a request for public-contract availability, not an additional skill.

## Fees, deposits, and collateral

EVE University’s [Contracts](https://wiki.eveuniversity.org/Contract) page describes item-exchange, auction, and courier terms and the public/private/corporation/alliance availability choices. V24.01 `contractscommon.py` is the exact fee formula for this client build: broker fee starts at 0.4% (10,000 ISK minimum, 100,000,000 ISK cap before Broker Relations reduction), sales tax starts at 1% (10,000 ISK minimum, 1,000,000,000 ISK cap before Accounting reduction), and public contracts (availability 0) have a deposit of 1% of base (100,000 ISK minimum, 100,000,000 ISK maximum; courier minimum 10,000 ISK). Private, corporation, and alliance availability (1 to 3) have no deposit. Courier costs use a 0.1 multiplier. Each expiry day after the first adds 0.05 percentage points to the broker rate and applies the separate 5% amount multiplier. These contract rules are separate from market broker fee and market sales-tax calculations.

The core V24 formula is implemented in `ContractFeesCalculator` and covered by `ContractFeesCalculatorTests`: it uses `max(price, reward)`, the type cost multiplier, skill reductions, fee minima/caps, the expiry-day broker increment, and private-contract deposit rules. Its `brokerFeeAdjustment` input is required so the caller supplies the client-equivalent system/faction adjustment from `facwarCommon.GetAdjustedFeePercentage` when applicable. Creation must persist the resulting amounts so later skill changes cannot rewrite a settlement. The calculator does not perform wallet charging or determine that adjustment. A deposit is returned on successful completion; deletion or expiry forfeits it as described by the contract rules. Courier collateral is separate from the creation deposit: it is secured from the hauler at acceptance, returned on successful delivery, and paid to the issuer on failure. Do not derive collateral from item prices; it is the amount specified by the issuer.

## RPC and notification boundary

The startup response fields above are confirmed. The client callsites establish `CreateContract(contractInfo, confirm=...)`, `AcceptContract(contractID, forCorp)`, `PlaceBid(contractID, bid, forCorp)`, `CompleteContract(contractID, status)`, and `DeleteContract(contractID)`. The broader read path includes `GetContract`, `SearchContracts`, `GetMyContractEscrow`, `CollectMyPageInfo`, item-location/container reads, and bid/history reads. Each response row shape must be copied from the V24 caller and shared contract row types before implementing that RPC. The service-response audit marks several EveJS-only methods with incomplete shapes; those methods stay out of scope until a V24 caller establishes the wire contract.

Publish the V24 contract notifications only after the corresponding transaction commits. Preserve notification order from the client and EveJS flows and ensure retries do not publish duplicate state transitions. Existing audit evidence establishes startup notification consumption; exact notification payloads for each mutation still need callsite/type confirmation before wiring.

## Delivery slices

1. **Creation slice built:** PostgreSQL contract header, escrow-item, requested-item, and bid tables; Host read wiring; and `CreateContract` RPC wiring for item exchange, auction, and courier terms. Creation validates station context, contract-manager role for corp issuance, availability, expiry, item/request limits, wallet account, fees, and personal/corporation capacity. The V24 fee formula core is unit-tested; the RPC currently supplies a neutral broker fee adjustment. The Host writer escrows offered items and issuer reward, broker fee, sales tax, and deposit, records requested terms, and performs an advisory-locked per-issuer outstanding-count check in the same PostgreSQL transaction. The listed price remains due from the acceptor at acceptance, following EveJS `prepareContractAcceptanceRequest`. The writer persists contract type, courier destination, duration, and collateral, and marks courier escrow items as packages. Settlement, fee adjustment from faction/system state, and full corp hangar authorization remain open.
2. Item exchange create/accept/delete/expiry with inventory and wallet journaling, notifications, failure and retry coverage.
3. Public/region search, corporation issuance and item selection, availability checks, and contract-limit enforcement.
4. Auction bids, outbid refunds, buyout/expiry settlement, and bid reads.
5. Courier accept/pickup/complete/fail with collateral, deadline expiry, and notifications.
6. Headless-client walk between two characters, restart reads, and comparison with the same EveJS scenario.

A slice is complete only when its wire values, persisted state, side effects, notification order, retry behavior, and focused tests are recorded in TODO and the compatibility ledger. The graphical V24 client is a separate verification target.
## Persisted terms migration

Migration `0002_contract_terms` extends the existing PostgreSQL header with availability, price, collateral, creation fee, deposit, sales tax, title, and description. Each monetary column is nonnegative with two-decimal precision, and availability is constrained to the four client modes (private, public, corporation, alliance). Existing rows receive zero-valued terms and remain readable. This schema supports later creation and settlement writes; it does not itself create contracts or move inventory or ISK.


## Advanced Contracting capacity rule

The creation path uses the client `NUM_CONTRACTS_BY_ADVSKILL` values directly alongside `NUM_CONTRACTS_BY_SKILL`; the personal limit is their table sum for the current skill levels. It does not use a `+4 per level` formula. The Host checks the resulting capacity under a transaction-scoped advisory lock. The table source is V24.01 `eve/common/script/util/contractscommon.py`.
