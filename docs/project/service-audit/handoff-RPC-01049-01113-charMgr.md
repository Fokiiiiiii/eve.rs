# charMgr RPC coverage handoff (RPC-01049–RPC-01113)

## Verified behavior and changes

The Host has working implementations for all listed remote methods except RPC-01092, which the client audit labels as a remote method but the whole client source shows is a local cache helper:

- GetPrivateInfoOnCorpChange reads the bound character and corporation membership start and returns a KeyVal with corporationID and corporationDateTime (CharMgrService.cs:848–866).
- GetRecentShipKillsAndLosses uses the bound character, validates pagination inputs, caps page size at 25, and returns the killmail page (CharMgrService.cs:1199–1221).
- GetSettingsInfo returns the settings code blob and signature integer as a two-element tuple (CharMgrService.cs:345). LogSettings validates bounded data and persists through the character read model (CharMgrService.cs:1084–1098).
- RemoveOwnerNote removes the stored note and preserves the final folders record (CharMgrService.cs:1103–1125).
- ListStations, ListStationItems, and ListIncludingContainers are registered charMgr methods delegating to the shared global-assets provider (CharMgrService.cs:307–320). The production provider is registered in CharacterRpc.cs:77–100; inventory rows come from persisted item state in InvBrokerService.cs:828–920.
- Added CharMgrRpcHostWireTests.cs: an actual Host socket and registered CharMgrService route exercise the settings tuple, global asset list routes, personal action replies, and transport-added machoVersion kwargs. The focused test passed: 1 passed, 0 skipped. The fixture does not use PostgreSQL.

## V24.01 client callers and consumers

- RPC-01049/01050: eve/client/script/ui/shared/neocom/wallet/panels/personal/personalDonateEMPanel.py:68–76. It rejects a None reply, reads corporationDateTime, computes membership days, and checks the player-corporation condition.
- RPC-01086–01089: eve/client/script/ui/shared/info/infosvc.py:2646–2652 requests the page twice and filters each reply by finalCharacterID or victimCharacterID. eve/client/script/ui/shared/neocom/charactersheet.py:90 invalidates the cached page.
- RPC-01090/01091 and RPC-01107/01108: carbonui/services/settingsSvc.py:148–155 verifies the two-part settings reply, loads the returned code, then logs settings statistics through LogSettings.
- RPC-01097: eve/client/script/ui/shared/assetsWindow.py:414 iterates the container-inclusive item list; the local inventory wrapper at eve/client/script/environment/invCache.py:877–888 caches the remote result.
- RPC-01098–01100: assetsWindow.py:744, market/quote.py:376, and assets/assetSafetySvc.py:35 consume station item rows. The five-minute wrapper and cache behavior are in invCache.py:839–854.
- RPC-01101–01106: callers are assetsWindow.py:440,922, neocom/assetsinfo.py:148, neocom/corporation/corp_ui_accounts.py:752, neocom/contracts/createContractWnd.py:1571, and market/quote.py:364. The wrapper returns cached station rows in invCache.py:826–838.
- RPC-01110–01113: eve/client/script/ui/shared/neocom/notepad.py:107,179,703 removes malformed, obsolete, and user-deleted note IDs; callers ignore the result.

## RPC-01092 classification

invItemFunctions.py:296–297 calls inv5MinCacheContainer.InvalidateStationItemsCache locally. Its implementation at invCache.py:855–859 deletes a client-side dictionary entry; it performs no service or moniker call. EveJS charMgrService.js:505–520 delegates the global asset remote methods, and charMgrGlobalAssets.js:544–584 implements the list reads; neither exposes InvalidateStationItemsCache. Adding a Host route would invent a server-side cache and side effect with no client request. Keep the audit row marked for correction as a client-local API instead of adding a no-op or fake-success handler.

## EveJS comparison

The corresponding EveJS paths are server/src/services/character/charMgrService.js:505–520 (global asset delegation), 574–589 (killmail page), 617–631 (corp membership info), 738–744 (settings code pair), 990–1001 (settings telemetry), and 1047–1054 (owner-note deletion). Global asset row generation is in server/src/services/character/charMgrGlobalAssets.js:544–584. These match the eve.rs reply purposes and persistence path; the client-local invalidation helper has no EveJS RPC counterpart.

## Verification limits

The focused Host wire test and Host/test-project compile-check were run locally. No PostgreSQL database or graphical EVE client was used. Existing CharacterInfo service tests cover character-bound state and persistence; the added no-database Host regression verifies registered routing and wire transport arguments.
