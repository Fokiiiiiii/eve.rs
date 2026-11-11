# Main CI red gate: auction buyout sales tax

## Decision

The issuer balance expectation for the registered buyout flow is **60,000 ISK**. The test initializes the issuer wallet at 50,000 ISK (`ContractAuctionBuyoutHostWireTests.cs:54-62`), uses a 20,000 ISK winning bid (`:79-99,153-155`), and leaves Accounting at the schema default of level 0 (`PersistenceContractReadModel.cs:129-132`). The minimum auction sales tax is 10,000 ISK. Issuer claim therefore adds 20,000 ISK of sale proceeds and deducts 10,000 ISK of tax. The later winner claim transfers the item and does not change the issuer wallet.

This retains the pending change in `ContractAuctionBuyoutHostWireTests.cs` that updates both issuer balance assertions from 70,000 to 60,000. The assertion now checks the tax-adjusted payout; it does not reduce the scenario or remove a check.

## V24.01 client evidence

- `/home/user/eve-logic/eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:654-661` renders auction tax as `PercentOfSalesPriceAtTax`; it computes the displayed maximum using the buyout, or the maximum price when there is no buyout.
- `/home/user/eve-logic/eve/common/script/util/contractscommon.py:13-15,84-94` defines the 1% base rate, 10,000 ISK minimum and Accounting reduction. At Accounting 0, a 20,000 ISK sale is below the minimum, so the tax is 10,000 ISK.
- `/home/user/eve-logic/eve/client/script/ui/shared/neocom/contracts/contracts.py:673-677` calls `FinishAuction(contractID, isIssuer)` as a separate RPC. `/home/user/eve-logic/eve/client/script/ui/shared/neocom/contracts/contractentry.py:202-211` calls it once for winner item delivery (`False`) and separately for issuer proceeds (`True`).
- `/home/user/eve-logic/eve/client/script/ui/shared/neocom/contracts/contracts.py:639-668` sends the bid amount and the corporation-wallet selection to `contractProxy.PlaceBid`.

## EveJS v0.12.9 evidence

The local reference is `/home/user/evejs-261108.1`.

- `server/src/services/_other/contractProxyService.js:886-907` routes `PlaceBid` and `FinishAuction` to the contract runtime.
- `server/src/services/contracts/contractRuntimeState.js:160-165,4666-4669` delegates those calls to the auction runtime and auction claims implementations.
- `server/src/services/contracts/contractRuntimeState.js:1150-1190` records the auction Accounting level and creates broker-fee/deposit terms. Its creation policy comment says the broker fee and deposit are charged at creation; the auction tax is not debited there.
- `server/src/services/contracts/contractAuctionClaims.js:63-110` handles the two claims independently. On the issuer claim (`side === "issuer"`), lines 89-98 calculate tax from the actual winning bid, subtract it from proceeds, and prepare the seller wallet leg. The winner claim creates item-transfer legs instead.

For this test's values, EveJS computes `max(10,000, trunc(20,000 * 0.01)) = 10,000 ISK`, then seller proceeds are 10,000 ISK before the separate deposit component. That matches the 60,000 ISK expected balance.

## eve.rs implementation and regression

- `dotnet/src/Eve.Host/Persistence/Contracts/ContractExpiryJobs.cs:189-213` calculates tax from the winning amount and saved Accounting level during issuer claim, caps it to the sale plus deposit, and writes sale proceeds, tax and deposit return as wallet entries.
- `dotnet/tests/Eve.Host.Tests/ContractAuctionBuyoutHostWireTests.cs:88-99,182-223` runs the registered Host RPC flow with a 20,000 ISK buyout and asserts the issuer balance after each independent claim.
- Main already contains commit `43f571e9d` (`Fix auction tax boundary regression test`). It updates the separate small-sale persistence assertion in `ContractProxyPersistenceWireTests.cs` to expect zero net payout when a 500 ISK sale is fully consumed by the capped tax. That fix was not duplicated here.

## RPC fuzz teardown finding

CI run 1701 (`75f852dc`, run [42939236490](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42939236490)) reported `RpcFuzzTests.Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` finding an `RpcFuzz` session `idle in transaction` while it read an `events.outbox` scratch schema. The test calls `HostRuntime.ShutdownAsync` before checking scratch-schema sessions in `dotnet/tests/Eve.Host.Tests/RpcFuzzTests.cs:173-205`; the outbox read is in `dotnet/src/Eve.Events/EventBus.cs:395-407`.

That failure did not recur in the green baseline run 1700 on `e4718ab1` ([42932626142](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42932626142)), which predates the auction change, or in run 1702 on `43f571e9` ([42944190912](https://github.com/Fokiiiiiii/eve.rs/actions/runs/42944190912)), where host test shard 6/6 passed. The focused test also passed locally on `eve_p3`. This was one observed idle-in-transaction teardown failure; the available runs do not establish its precise cause, so no test or EventBus change was made.

## Verification and limits

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --configuration Release --no-restore --warnaserror`: passed, 0 warnings and 0 errors.
- `FullyQualifiedName~Contract` on `eve_p3`: 43 passed, 0 failed, 0 skipped.
- `RpcFuzzTests.Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` on `eve_p3`: 1 passed, 0 failed, 0 skipped.
- The EveJS code was traced but its server process was not run for this scenario. The unmodified graphical client and a packet capture were not used.
