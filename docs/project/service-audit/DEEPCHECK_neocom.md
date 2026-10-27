# Deep check: neocom

## Finding

The V24.01 client has a same-named **local UI service**, not a remote RPC service. `eve/client/script/ui/shared/neocom/neocom/neocomSvc.py:42-43` defines `NeocomSvc(Service)` with `__guid__ = 'svc.neocom'`. Whole-tree callsite searches find `sm.GetService('neocom')`, `ServiceManager.Instance().GetService('neocom')`, and local aliases. They find no `RemoteSvc('neocom')`, bound-object/moniker lookup for `neocom`, or remote `neocom` call.

The Host graph has no `neocom` route (`Route` query: zero rows); Host code search has no handler. EveJS 261018.1 (indexed source path: `server/src/services`) has no `neocom` service or method (service-method graph query and exact `'neocom'` source search: zero rows). Thus no Host method, wire signature, error packet, persisted server state or server notification is specified by this client service. The nearest client match is the local `svc.neocom` UI service itself. Adjacent UI modules call their own remotes, e.g. `charMgr` at `eve/client/script/ui/shared/neocom/charactersheet.py:58`, `petitioner` at `eve/client/script/ui/shared/neocom/help.py:56`, `account` at `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:40`, `calendarMgr` at `eve/client/script/ui/services/eveCalendarsvc.py:63`, and `mailMgr` at `eve/client/script/ui/services/mail/mailSvc.py:78`. These names do not map to `neocom`.

For every method in the inventory below, Host route reachability, Macho argument encoding, transport return shape, UserError/exception packet, server persistence and server notification are **not applicable**: each call dispatches to the in-process client object. No Host wire test was added because it would invent a remote method that the V24.01 client does not call.

## Client-called methods

Call sites were searched across the full `/home/user/eve-logic` tree, including direct service-manager access and aliases (`neocomSvc`, `neocom_service`). Method declarations are in `eve/client/script/ui/shared/neocom/neocom/neocomSvc.py`; the `svc.neocom` identity is at lines 42-43.

| Method | Client definition | Whole-client call sites |
|---|---:|---|
| `AddButtonToRoot` | `neocomSvc.py:801` | `eve/client/script/ui/shared/neocom/neocom/neocomPanelEntries.py:169` |
| `Blink` | `neocomSvc.py:593` | `eve/client/script/ui/shared/newFeatureNotifyWnd.py:196`; `eve/client/script/ui/shared/planet/planetUISvc.py:176`; `eve/client/script/ui/shared/neocom/charactersheet.py:75`; `eve/client/script/ui/shared/neocom/petition.py:77,90,137,951`; `eve/client/script/ui/shared/neocom/journal.py:181`; `eve/client/script/ui/shared/neocom/neocom/btnData/btnDataNode.py:443`; `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:59,72,77,93,108,118`; `eve/client/script/ui/shared/neocom/contracts/contracts.py:282`; `eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderStorylineAgents.py:54`; `eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderEscalations.py:65`; `eve/client/script/ui/services/marketsvc.py:125`; `eve/client/script/ui/services/eveCalendarsvc.py:346,366`; `eve/client/script/ui/services/mail/mailSvc.py:1202,1278`; `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:468`; `eve/client/script/environment/planet/clientPlanet.py:188`; `seasons/client/seasonservice.py:216`; `sovereignty/mercenaryden/client/ui/containers/cargo.py:205`; `eve/client/script/ui/services/uipointersvc.py:517`; internal self-call `neocomSvc.py:886` |
| `BlinkOff` | `neocomSvc.py:607` | `eve/client/script/ui/eveCommands.py:2743`; `eve/client/script/ui/shared/eveCalendar.py:74`; `eve/client/script/ui/shared/neocom/help.py:134`; `eve/client/script/ui/shared/neocom/petition.py:235`; `eve/client/script/ui/shared/neocom/journal.py:560`; `eve/client/script/ui/shared/neocom/wallet/walletWnd.py:40`; `eve/client/script/ui/shared/redeem/window.py:66`; `eve/client/script/ui/services/mail/mailSvc.py:288` |
| `CloseAllPanels` | `neocomSvc.py:669` | `eve/client/script/ui/shared/neocom/neocom/neocomPanelEntries.py:145,434,458`; `eve/client/script/ui/shared/neocom/neocom/neocomContainer.py:421`; `eve/client/script/ui/shared/neocom/neocom/buttons/overflowButton.py:59`; `eve/client/script/ui/shared/neocom/neocom/buttons/buttonEveMenu.py:69`; `eve/client/script/ui/shared/neocom/neocom/buttons/baseNeocomButton.py:392` |
| `CloseChildrenPanels` | `neocomSvc.py:681` | `eve/client/script/ui/shared/neocom/neocom/neocomPanels.py:315`; `eve/client/script/ui/shared/neocom/neocom/neocomPanelEntries.py:199,286,372,374` |
| `ClosePanel` | `neocomSvc.py:676` | `eve/client/script/ui/shared/neocom/neocom/neocomPanelEntries.py:440` |
| `CreateNeocom` | `neocomSvc.py:255` | `eve/client/script/ui/shared/sidePanelsLayer.py:38` |
| `FindParentGroupBtn` | `neocomSvc.py:872` | `eve/client/script/ui/services/uipointersvc.py:538` |
| `GetBtnData` | `neocomSvc.py:569` | `eve/client/script/ui/shared/neocom/neocom/fixedButtonExtension.py:100`; `eve/client/script/ui/shared/pointerTool/helpPointerSvc.py:315` |
| `GetEveMenuBtnDataRoot` | `neocomSvc.py:487` | `eve/client/script/ui/shared/neocom/neocom/buttons/buttonGroup.py:57`; `eve/client/script/ui/shared/neocom/neocom/btnData/btnDataNodeGroup.py:21` |
| `GetEveMenuButtonSize` | `neocomSvc.py:701` | `eve/client/script/ui/services/uipointersvc.py:401` |
| `GetFixedButtonData` | `neocomSvc.py:532` | `eve/client/script/ui/shared/pointerTool/helpPointerSvc.py:260` |
| `GetMenu` | `neocomSvc.py:828` | `eve/client/script/ui/shared/neocom/neocom/neocomContainer.py:303`; `eve/client/script/ui/shared/neocom/neocom/buttons/wrapperButton.py:105`; `eve/client/script/ui/shared/neocom/neocom/buttons/buttonEveMenu.py:82` |
| `GetMinimizeToPos` | `neocomSvc.py:535` | `carbonui/control/window.py:1904` |
| `GetNeocomContainer` | `neocomSvc.py:309` | `eve/client/script/ui/shared/neocom/neocom/neocomPanels.py:145`; `eve/client/script/ui/shared/neocom/neocom/buttons/overflowButton.py:72`; `eve/client/script/ui/shared/maps/browserwindow.py:116`; `eve/client/script/ui/services/uipointersvc.py:383,545`; `eve/client/script/ui/services/windowsvc.py:251`; `eve/devtools/script/form_tournament.py:330-331` |
| `GetTopLevelBtnData` | `neocomSvc.py:572` | `eve/client/script/ui/shared/neocom/neocom/btnData/btnDataNode.py:530` |
| `GetUIObjectAndNodeByID` | `neocomSvc.py:713` | `eve/client/script/ui/services/uipointersvc.py:532` |
| `GetUIObjectByUniqueUiName` | `neocomSvc.py:734` | `eve/client/script/ui/services/uipointersvc.py:541` |
| `GetUnseenInventoryItemsCount` | `neocomSvc.py:347` | `eve/client/script/ui/shared/inventory/neocomInventoryBadging.py:41` |
| `HasUnseenInventoryItemsInLocation` | `neocomSvc.py:352` | `eve/client/script/ui/control/treeViewEntry.py:525`; `eve/client/script/ui/shared/dockedUI/inventory.py:92-93` |
| `IsAvailable` | `neocomSvc.py:326` | `eve/client/script/ui/shared/systemMenu/systemmenu.py:2311`; `carbonui/window/control/action.py:242` |
| `IsBtnInOverflowList` | `neocomSvc.py:563` | `eve/client/script/ui/services/uipointersvc.py:544` |
| `IsButtonVisible` | `neocomSvc.py:751` | `eve/client/script/ui/services/uipointersvc.py:516` |
| `IsInventoryItemUnseen` | `neocomSvc.py:357` | `eve/client/script/ui/shared/item.py:275` |
| `IsSomePanelOpen` | `neocomSvc.py:657` | `eve/client/script/ui/shared/neocom/neocom/neocomContainer.py:464` |
| `IsValidDropData` | `neocomSvc.py:825` | `eve/client/script/ui/shared/neocom/neocom/neocomPanelEntries.py:361`; `eve/client/script/ui/shared/neocom/neocom/neocomContainer.py:348`; `eve/client/script/ui/shared/neocom/neocom/buttons/buttonWindow.py:87,101`; `eve/client/script/ui/shared/neocom/neocom/buttons/buttonGroup.py:39,52`; `eve/client/script/ui/shared/neocom/neocom/buttons/baseNeocomButton.py:373,380` |
| `MarkInventoryItemSeen` | `neocomSvc.py:362` | `eve/client/script/ui/shared/item.py:260` |
| `Minimize` | no definition in `NeocomSvc` | `eve/client/script/ui/station/base.py:390` |
| `OnBtnDataDropped` | `neocomSvc.py:782` | `eve/client/script/ui/shared/neocom/neocom/neocomContainer.py:350`; `eve/client/script/ui/shared/neocom/neocom/buttons/buttonWindow.py:103`; `eve/client/script/ui/shared/neocom/neocom/buttons/baseNeocomButton.py:376` |
| `OnButtonDragEnter` | `neocomSvc.py:767` | `eve/client/script/ui/shared/neocom/neocom/neocomContainer.py:362` |
| `OnButtonDragExit` | `neocomSvc.py:778` | `eve/client/script/ui/shared/neocom/neocom/buttons/baseNeocomButton.py:387` |
| `OnNeocomButtonsRecreated` | `neocomSvc.py:616` | `eve/client/script/ui/shared/neocom/neocom/neocomContainer.py:248` |
| `OnNeocomTaskbarMouseEnter` | `neocomSvc.py:761` | `eve/client/script/ui/shared/neocom/neocom/neocomContainer.py:354` |
| `RegisterFixedButtonExtension` | `neocomSvc.py:494` | `loginrewards/client/loginCampaignService.py:99`; `eve/client/script/ui/services/redeemsvc.py:43`; `eve/client/script/ui/clonegrade/upsell_neocom_controller.py:75`; `contextualOffers/client/contextualOffersSvc.py:42` |
| `Reload` | `neocomSvc.py:130` | `eve/devtools/script/windowManager.py:187` |
| `SetLocationInfoState` | no definition in `NeocomSvc` | `eve/client/script/ui/station/base.py:394,405` |
| `ShowEveMenu` | `neocomSvc.py:706` | `eve/client/script/ui/shared/neocom/neocom/buttons/buttonEveMenu.py:74` |
| `ShowPanel` | `neocomSvc.py:620` | `eve/client/script/ui/shared/neocom/neocom/neocomPanelEntries.py:291,375,443`; `eve/client/script/ui/shared/neocom/neocom/buttons/overflowButton.py:63`; `eve/client/script/ui/shared/neocom/neocom/buttons/baseNeocomButton.py:396` |
| `ToggleAuraGuidanceIndicator` | `neocomSvc.py:177` | `auraguidance/client/ui/window.py:75` |
| `ToggleEveMenu` | `neocomSvc.py:693` | `eve/client/script/ui/eveCommands.py:1867` |
| `UnregisterFixedButtonExtension` | `neocomSvc.py:502` | `eve/client/script/ui/clonegrade/upsell_neocom_controller.py:83`; `contextualOffers/client/contextualOffersSvc.py:48` |
| `UpdateNeocomButtons` | `neocomSvc.py:453` | `eve/client/script/ui/shared/mapView/settings.py:11` |

`NeocomSvc.HasUnseenInventoryItems` is defined at `neocomSvc.py:342`, but whole-tree service-access searches found no client caller.

## Local argument, result, state and event contracts

These are ordinary Python calls to a local object. For example, `Blink(self, wndID, hint=None)` accepts one or two positional arguments; one-argument calls include `eve/client/script/ui/shared/newFeatureNotifyWnd.py:196`, and two-argument calls include `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:59`. `ShowPanel(self, triggerCont, panelClass, panelAlign, *args, **kw)` is called with both positional and keyword arguments at `eve/client/script/ui/shared/neocom/neocom/neocomPanelEntries.py:291,375,443`. Inventory calls use keyword arguments at `eve/client/script/ui/shared/item.py:260,275`. These paths carry no `machoVersion`, PyInt/PyLong/PyNone encoding, or serialized optional value.

The UI consumes local values directly: `GetMinimizeToPos` returns an `(x, y)` pair unpacked at `carbonui/control/window.py:1904`; `GetEveMenuButtonSize` returns a size pair unpacked at `eve/client/script/ui/services/uipointersvc.py:401`; `GetUIObjectAndNodeByID` returns an object/node pair consumed at `eve/client/script/ui/services/uipointersvc.py:532`; boolean queries feed conditions at `eve/client/script/ui/services/uipointersvc.py:516,544`, `eve/client/script/ui/shared/item.py:275`, `eve/client/script/ui/control/treeViewEntry.py:525`, and `eve/client/script/ui/shared/systemMenu/systemmenu.py:2311`; `GetNeocomContainer` returns a UI container used directly by callers listed above. These are in-process Python results, not wire reply types. The local error path includes `RegisterFixedButtonExtension` raising `ValueError('extension already registered')` at `neocomSvc.py:494-501`; no client `UserError` contract maps to a Host exception for these calls.

The service's UI layout reads/writes `settings.char.ui` (`neocomSvc.py:198-209,246-250`). Runtime panel, blink-queue and button-root state is held on the client service instance and reset during lifecycle transitions (`neocomSvc.py:56-75,111-128`). It is not authoritative eve.rs state and needs no Host persistence.

Expected signaling is local UI signaling: `CreateNeocom` scatters `OnNeocomAvailableChanged` (`neocomSvc.py:255-279`); inventory-badging setup/teardown scatters `OnInventoryBadgingCreated` and `OnInventoryBadgingDestroyed` (`neocomSvc.py:329-339`); `ShowEveMenu` scatters `OnEveMenuShown` (`neocomSvc.py:706-712`). Client components also call `Blink` after their own updates, e.g. wallet balance handling at `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:93` and new mail handling at `eve/client/script/ui/services/mail/mailSvc.py:1202`. These are local client callbacks/UI effects; no server notification is expected from a `neocom` Host method.

## Verification

- Whole-client searches covered direct service-manager calls and service aliases; the caller inventory is above.
- CBM graph search/route query found no Host `neocom` route or EveJS `neocom` service method. Graph call resolution only linked local calls such as `_ApplyBlinkQueue -> Blink`; dynamic `GetService` usages were enumerated with Serena's full-tree pattern search.
- No database was used. No implementation gap in a client-callable Host `neocom` service was found, so no Host wire test or code fix applies.
- Graphical-client execution was not part of this audit.
