# Deep fix: SKINs, redemption and paperdoll

## Findings and evidence

### Ship SKIN license activation

The V24 menu offers license activation from `menucheckers/itemCheckers.py:55-57`; character-sheet, tooltip, and menu callers are in `eve/client/script/ui/shared/neocom/charsheet/skinsPanel.py:170-174`, `eve/client/script/ui/shared/tooltip/itemBtns.py:115,161`, and `eve/client/script/ui/services/menusvc.py:290-292,969-977`. `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:395-420` confirms single-use, permanent, limited-use, and batch activation through `cosmeticsSvc`; `cosmetics/client/cosmeticsSvc.py:89-92` forwards to `shipCosmeticsMgr`, and the license state transition is in `cosmetics/client/shipSkinLicensesSvc.py:94-100`. The client consumes the activation signal in `cosmetics/client/cosmeticsSvc.py:213-216`, `eve/client/script/ui/shared/skins/controller.py:237-245`, and `eve/client/script/ui/shared/neocom/charsheet/skinsPanel.py:153-156`. Activation must be a single-use state transition; concurrent requests against one license previously both succeeded.

The Host now serializes activation by character and rechecks the license under the lock before consuming it. `ShipCosmeticsMgrRpcServiceTests.Concurrent_skin_license_activation_consumes_only_one_copy_for_one_character_license` reproduced two successful activations before the fix and passes after it.

### Store-delivered token items

The V24 redemption UI reads token rows and constructs requested token IDs in `eve/client/script/ui/shared/redeem/redeemItemsContainer.py:343-354`; inventory token rows are keyed in `:401-402`, and redeem inventory behavior is in `eve/client/script/environment/invControllers.py:1166-1214`. Host compatibility projects a redundant mass-token ID as `None` when it equals the normal token ID (`UserSvcCompatibility.cs:244-260`). A client-shaped row and a tuple row could therefore name one token ID twice and grant two items. The store now deduplicates by token ID before mutation.

`NewEdenStoreHostTests.Reverse_redeeming_plex_moves_the_item_to_a_station_locked_token_and_claims_it_once` failed before the fix with two items and passes with the duplicate represented in both accepted row shapes. Current EveJS 261028.1 `server/src/services/user/userService.js:454-473,823-1057` does not deduplicate this case; this safety correction preserves the V24 row shape and prevents duplicate grants.

### Character resculpt and paperdoll state

The menu call path is `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:387-392` and `eve/client/script/ui/services/menusvc.py:3499-3500`; an item double-click caller is `eve/client/script/ui/shared/item.py:815-817`. The activation boolean is ignored by the client. Character selection reads paperdoll state in `loginCharacterselection/charselData.py:170-171`; state values are defined in `paperdoll/common/state.py:8-12`, and `characterCreationContainer_new.py:274-324` maps states 1/2/4 to full edit and state 3 to gender-capable resculpt. The station caches `GetPaperdollState` and clears it on `OnCharacterHandler` (`eve/client/script/ui/station/base.py:194-204`). Appearance-save paths and UI refresh include `eve/client/script/ui/login/charSelection/characterSelection.py:117-121`. EveJS orders item change before `OnCharacterHandler` (`server/src/services/character/characterResculpt.js:51-67,93-96`).

Pinned SDE items 34132 and 89956 distinguish gender-capable and no-gender certificates. Activation now binds to the selected session character; requires the item in its current station hangar or active ship cargo; consumes one stack unit; and persists state 3 for 34132, state 1 for 89956. Both states clear on appearance save. The client maps certificate state 1 to face/body edits without gender and state 3 to gender-inclusive resculpt. This intentionally differs from EveJS 261028.1 `characterResculpt.js:98-123`, which sets state 1 for either certificate. The SDE distinctions and V24 editor mappings take priority.

Before the fixes, tests reproduced: foreign-character activation success; remote-hangar activation success; two concurrent successes; whole-stack deletion; state-2 certificate activation; and missing state-1 stack activation. Focused Host coverage now checks ownership, inventory scope, stack decrement, both state values, save completion, restart persistence, concurrency, and notification order (`OnItemChange` then `OnCharacterHandler`). Refusal returns `False`, matching the client path that ignores the result; there is no player-visible error code on this action.

## Sources

- EVE University: [New Eden Store](https://wiki.eveuniversity.org/New_Eden_Store), [Character Creator](https://wiki.eveuniversity.org/Character_Creator).
- CCP: [Pilot's Body Resculpt Certificate](https://support.eveonline.com/hc/en-us/articles/203411042-Pilot-s-Body-Resculpt-Certificate).
- Static item records: pinned SDE type IDs 34132 and 89956 in the local `eve_p3` database.

## Verification record

- `CharacterReSculptHostTests` passed on `eve_p3`: 7 passed, 0 failed or skipped.
- The combined touched Host classes passed on `eve_p3`: 72 passed, 0 failed or skipped. `Eve.Services.CharacterInfo.Tests` Paperdoll filter: 2 passed, 0 failed or skipped.
- Builds passed for `Eve.Characters`, `Eve.Services.Core`, `Eve.Host`, and `Eve.Host.Tests`, with zero warnings or errors. Main CI and push-triggered CI checks are pending.
- Graphical V24 client flow was not exercised in this worktree.
