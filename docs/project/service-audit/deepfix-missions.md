# Deep fix: missions, agents, standings, and LP wallet

## Change and result

The concrete defect repaired in this pass was that both client LP donation RPCs were advertised by `LPSvc` but always refused by eve.rs. The V24.01 player and corporation donation panels call these methods directly. The repair validates the request and recipient, verifies persisted director authority for corporation donations, commits the debit and credit together, and announces both committed balances before returning `None`.

## Client evidence and wire contract

The whole `/home/user/eve-logic` search found the wallet service methods at `eve/client/script/ui/shared/neocom/wallet/loyaltyPointsWalletSvc.py:93-99`. Its only donation callsites are the personal panel at `eve/client/script/ui/shared/neocom/wallet/panels/personal/personalDonateLPPanel.py:44-46` and corporation panel at `.../corp/corpDonateLPPanel.py:45-47`. Both pass positional `(receiverCorpID, issuerCorpID, lpAmount)`; neither consumes a reply value.

The client asks for confirmation and calls the selected panel method in `eve/client/script/ui/shared/neocom/wallet/panels/donateLPPanel.py:355-385`. Success shows the success label. A `UserError` is re-raised for the client error UI; other exceptions show the generic transfer failure. The personal panel filters to player corporations with at least one member (`personalDonateLPPanel.py:31-37`); the corporation panel also excludes the source corporation (`corpDonateLPPanel.py:36-41`). The server repeats these trust-boundary checks.

The response is `None`, matching the current EveJS handlers. The wallet balance notifications are consumed in `loyaltyPointsWalletSvc.py:58-66`: `OnLPChange` invalidates the character balance cache and emits `OnCharacterLPBalanceChange_Local`; `OnCorpLPChange` invalidates the corporation cache and emits `OnCorporationLPBalanceChange_Local`. Each donation panel refreshes its balance on the corresponding local event.

The transfer route rejects issuer corporation `1000419` (Evermarks). The client defines it as `corpHeraldry` in `eve/common/script/util/appConst.py:637`; generic LP balance lists omit that issuer in `loyaltyPointsWalletSvc.py:80,91`. The separate Evermark panel subclasses do not override `_do_transfer`, whose base implementation is a stub at `eve/client/script/ui/shared/neocom/wallet/panels/donateLPPanel.py:382-384`. EveJS 261028.1 does share the transfer handlers with Evermarks (`lpService.js:116-148`) and applies extra brand-manager/destination rules (`lpWalletState.js:615-769`). eve.rs therefore rejects Evermarks at the normal RPC boundary; the special Evermark flow remains open rather than bypassing those rules.

## Reference behavior and mechanics

EveJS 261028.1 handles the personal and corporation methods in `/home/user/evejs-261028.1/src/server/src/services/corporation/lpService.js:116-148`. The wallet helpers at `lpWalletState.js:615-688` and `:690-769` check positive arguments, recipient eligibility, source balance, and director role for corporation funds; successful paths debit and credit and return null. The error-to-`CustomNotify` messages are in `lpService.js:54-68`; eve.rs now uses the same player-facing messages.

EVE University documents LP as issuer-specific and confirms player-corporation LP donations on [Loyalty Points](https://wiki.eveuniversity.org/Loyalty_Points). The current implementation uses eve.rs's persisted corporation records and member store to determine that the recipient exists, is a player corporation, and has members. It does not infer eligibility from the request or a client session cache.

## eve.rs path

- Host registration is in `dotnet/src/Eve.Host/Rpc/AgentRpc.cs:80-90`. The character's source corporation comes from the persisted character store; the recipient check reads `CorporationStore.GetAsync` and `MembersAsync`; corporation-wallet spending uses the existing persisted director check.
- `dotnet/src/Eve.Services.Wallet/LoyaltyPointsService.cs:43-115` validates the positional inputs and authority, maps refusals to the reference `CustomNotify` text, and returns `PyNone` on success.
- `dotnet/src/Eve.Wallet/Loyalty/LoyaltyPointStore.cs` holds the transaction: it applies the debit and credit with journal references in one transaction, locks balances in deterministic holder order, commits, then announces source followed by destination. The reference uses the Macho session and call IDs, so replaying the same call cannot spend twice.
- `dotnet/src/Eve.Host/Composition/CharacterComposition.cs:206` registers the existing `LoyaltyPointAnnouncer`. Its committed balance events send the client notifications.
- The exchange method remains a refusal; this repair did not add exchange-rate mechanics.

## Regression checks

- `dotnet/tests/Eve.Host.Tests/LpStoreRpcHostWireTests.cs`: Host RPC wire test covers both positional donation calls, `None` replies, corporate-wallet authorization failure text, and the still-refused exchange route.
- `dotnet/tests/Eve.Services.Wallet.Tests/LoyaltyPointsServiceTests.cs`: database-backed personal donation covers both persisted balances, replay, and exact notification order.
- `dotnet/tests/Eve.Wallet.Tests/LoyaltyPointStoreTests.cs`: database-backed transfer covers atomic balances, idempotent replay, insufficient funds rollback, source-before-destination announcements, and concurrent spending from one source.

## Additional review and remaining scope

The client mission dialog enters through bound `agentMgr.DoAction` at `eve/client/script/ui/station/agents/agentDialogueWindow.py:427`. eve.rs dispatches through `AgentMissionService.DoActionAsync` and `AgentDialogue.StepAsync`; EveJS's matching entry and offer acceptance are `agentMgrService.js:899-1120` and `agentMissionRuntime.js:6427-6500`. This pass did not find a concrete defect in the request/offer/accept slice. The client standings gate is `standingsvc.py:99-103` calling `eve/common/script/sys/eveCfg.py:111-137`; eve.rs's matching threshold and bonus calculation is `AgentAccessRule.cs`. No numeric threshold mismatch was found in that read-only comparison.

The mission objective-completion and reward-settlement path, standings transaction history and compositions, and a graphical-client run were not exercised in this pass. They remain outside the verified scope of this change.
