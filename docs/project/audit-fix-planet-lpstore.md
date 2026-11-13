# Planet command-center stacks and LP-store item requirements

Audit date: 2026-11-13
Verdicts: **H-09 confirmed; H-04 confirmed.**

## H-09: command-center stacks

**Client evidence.** V24.01 `eve/client/script/environment/planet/clientPlanet.py:194-224` searches the ship's specialized command-center hold, colony-resources hold, and cargo for the requested type, then sends the found `itemID` as the command pin ID. `planetSvc.py:130-140` reloads planet pins on `OnMajorPlanetStateUpdate`, while the client inventory cache applies item quantity changes from `OnItemChange` (`invCache.py:219-247`).

**EveJS reference.** `server/src/services/planet/planetMgrService.js:974-1002` moves a singleton or one-unit item to the planet. For a stack, `:1004-1041` consumes quantity one and synchronizes the resulting inventory change to the client. The existing C# path deleted the source row regardless of its stack size, confirming the reported loss.

**Fix.** In `dotnet/src/Eve.Services.Planets/PlanetMgrService.cs`, a non-singleton stack with quantity greater than one now has its quantity reduced by one in the same transaction as the planet update. The committed change is sent as `OnItemChange` with the updated packed item row and the prior quantity, before the planet-state notice and RPC reply.

**Regression evidence.** `Registered_planet_update_consumes_one_command_center_from_a_stack_and_keeps_the_remainder` is a database-backed test that calls the registered `planetMgr` service over the Host wire. Before the quantity fix it failed because the original inventory item had been deleted. It now verifies the `OnItemChange` wire address and quantity delta, the `OnMajorPlanetStateUpdate` ordering, persisted stack quantity 3 from an initial 4, and the persisted command-center pin using the same item ID. A second placement is refused and leaves both inventory stacks and the colony unchanged.

**Remaining difference.** EveJS moves a singleton or quantity-one command center to the planet's specialized hold. The existing C# quantity-one/singleton branch still deletes that item; this stack-loss fix does not change that separate behavior. This path was not covered by the new regression test.

## H-04: LP-store item requirements

**Client evidence.** V24.01 `eve/client/script/ui/station/loyaltyPointStore/lpStoreSvc.py:306-318` implements `HaveItem` through `GetInvenoryItemQuantity`; the latter ignores singleton rows and records the largest `stacksize` for each type, not the sum of stacks. `lpOfferEntry2.py:55-100` uses that quantity to calculate purchaseable offers and to check whether requirements are met. `lpStoreSvc.py:248-260` calls `TakeOfferForCharacter` or `TakeOfferForCorporation`; `:425-434` invalidates the hangar cache after server item-change processing, and `:258-260` invalidates it after a purchase with required items.

**EveJS comparison.** `server/src/services/evermarks/lpStorePurchaseState.js:98-140` counts a singleton as one unit and selects requirement legs across multiple stacks. That behavior differs from the V24.01 client's eligibility calculation. The server now follows the client rule: only non-singleton stacks count, and only the largest single stack of a required type can satisfy that type's quantity.

**Fix.** In `dotnet/src/Eve.Services.Wallet/LpStorePurchases.cs`, the purchase transaction locks the relevant hangar rows, selects the largest positive non-singleton stack for each required type, refuses an insufficient stack, and consumes the requirement from that one stack. Singleton rows and quantities spread across smaller stacks remain untouched.

**Regression evidence.** `Registered_lp_store_uses_only_the_largest_non_singleton_hangar_stack_for_requirements` exercises the registered `LPStoreMgr` service over the Host wire and uses PostgreSQL persistence. Its first scenario has one singleton and two separate one-unit stacks for a two-unit requirement. Before the fix, the test expected an `ErrorResponse` but received a successful `CallRsp`; afterward the request is refused, all three rows remain unchanged, and no purchase row is recorded. Its success scenario purchases from a single three-unit stack, verifies that one unit remains, checks the granted item and persisted purchase row, and checks both `OnItemChange` notifications arrive before the RPC reply. A concurrent pair of further purchases against a new three-unit stack produces one success and one refusal; the stack is charged once, and only the successful purchase and reward are persisted.

## Verification limits

- These regressions use the real Host registration, RPC wire encoding, and PostgreSQL-backed stores on the isolated `eve_p4` database.
- No graphical V24.01 client session was run.
- The command-center source-location/flag validation visible in the client and EveJS was not changed or covered by this audit fix.
