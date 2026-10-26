# Core service path re-check

## 2026-10-26 checkpoint

This is a continuation record. The cross-service audit is not closed; only the findings below have been traced far enough to classify.

### Market: five M1 handlers confirmed present

- Client calls: V24.01 `marketsvc.py:253-295` routes through `marketProxy`. `BuyMulti` returns the `ordersCreated` sequence and immediately reads each row's `typeID` (`marketsvc.py:270-273`); `SellMulti` and `SellPlex` return their proxy values, while cancel/modify calls consume no result (`marketsvc.py:275-295`). Transaction rows are read by client code in `marketsvc.py:41-64`.
- Host handlers: `MarketProxyService.cs:560-645` implements `BuyMultipleItems`, `PlacePlexSellOrder`, `ModifyPlexCharOrder`, `CharGetTransactions`, and `CorpGetTransactions`. The Host method table and production registration checks are in `MarketHostTests.cs:28-34` and `MarketRpcHostWireTests.cs:90-105`; other market Host wire cases are in `MarketTradeHostWireTests.cs`.
- Reference: EveJS 261018.1 `server/src/services/market/marketProxyService.js` implements the corresponding proxy operations; its transaction entry builder is compared against the V24 fields consumed at `marketsvc.py:41-64`.
- Reconciliation: `docs/project/TODO.md:1084` is historical M1 completion context, not a current missing-handler claim. The methods are present and Host-registered. M1 tests and the listed live market walk cover earlier work; this re-check has not replayed all five through the current production Host and compared each persisted transition to EveJS.

### Mail: six client operations do not reach a handler

- Client calls in the whole V24.01 mail service include `mailMgr.EmptyTrash` (`mailSvc.py:458-476`), `DeleteMail` (`mailSvc.py:478-491`), `CreateLabel` (`mailSvc.py:727-737`), `EditLabel` (`mailSvc.py:711-725`), `DeleteLabel` (`mailSvc.py:739-754`), and `AssignLabels` (`mailSvc.py:756-772`). These methods update the client header/body caches; `CreateLabel` also consumes the returned label ID. Mailbox synchronization consumes `newMail`, `oldMail`, and `mailStatus`, including each status row's `messageID`, `statusMask`, and `labelMask` (`mailSvc.py:156-234`).
- Current Host surface: `MailService.HasMethod` in `dotnet/src/Eve.Services.Core/MailService.cs:44-50` omits all six names. `CallAsync` therefore does not route these calls to persistence. `MailboxMutation.Apply` in the same file handles status and label-mask changes only; it does not remove trashed/deleted statuses, add labels, or persist label definitions.
- Reference: EveJS 261018.1 `mailMgrService.js:429-518` routes these operations to `mailState.js` mutations (including returned label IDs and deletion/update notifications). This gap needs service callbacks, persisted mutations and Host wire assertions. It is not closed by the current `MailRpcHostWireTests.cs`, which exercises the currently supported mutations only.
- The chat-poke path is different: V24 calls it from `xmppchatsvc.py:1794-1795`; Host composition supplies `mailSendWriter.SendGmChatPokeAsync` in `MailRpc.cs:144-153`; EveJS composes the same titled mail in `mailState.js:2159-2176`.

### Other paths: inventory evidence gathered; full per-call review remains open

- Mailing lists: client RPC calls are in `mailingListsSvc.py:42-191`. Consumers use `GetInfo().displayName`, `Join().id`, the member map keys, and welcome-mail return values (`mailingListsSvc.py:55-108,141-193`). Host wire tests exist in `MailRpcHostWireTests.cs` and `MailingListRpcWireTests.cs`; the full call/result matrix still needs comparison to EveJS.
- Fleet: the V24 service entry points and client transitions are in `fleetSvc.py:254-383`, with fleet-proxy and settings paths later in that file. Host wire coverage is split across `FleetObjectRpcWireTests.cs`, `FleetRows01559To01610HostWireTests.cs`, `FleetDamageSubscriptionHostWireTests.cs`, and `PersistenceFleetSettingsTests.cs`. This inventory does not yet certify every caller, consumed field, or EveJS transition.
- Chat and LSC: client `XmppChatMgr` calls are in `xmppchatsvc.py:283,311,349,996-1007,1082,1123-1134,1197-1204,1257,1484,1817-1818`; the channel window also calls `GrantChannelOwnership` in `xmppchannelsettings.py:124`. `XmppChatMgrServiceTests.cs` covers hostname, channel names, private channels and resync. XMPP room discovery and client-side member/message consumption are in `xmppchatsvc.py:461-630,909-945,1013-1172`. The separate LSC channel path and all XMPP consumer fields have not yet been reconciled against Host and EveJS.
- Contacts and notes: V24 loads personal, corporation and alliance contacts from the `GetContactList` result's `addresses` and `blocked` rows, consuming `contactID`, `inWatchlist`, `relationshipID`, `labelMask`, and `senderID` (`addressbookService.py:193-250`); notepad bootstrap consumes `noteID` and `label` (`notepad.py:86-140`). Host persistence/read-model coverage exists in `HostServerTests.cs:435-649`. A full call-by-call notification and restart comparison remains open.
