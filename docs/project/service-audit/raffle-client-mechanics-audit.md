# Raffle client prize mechanics audit

This read-only audit compares the V24.01 client’s prize validation with the public HyperNet rules and the pinned client SDE. It records per-type and per-item checks; a category allowlist is not an equivalent contract.

## Authoritative HyperNet rules

- CCP’s [HyperNet Relay support article](https://support.eveonline.com/hc/en-us/articles/360010743940-HyperNet-Relay) says offers can be created only in NPC stations. It lists repackaged items, Blueprint Copies, Ancient Relics, and Abyssal Modules as offerable, and excludes containers or any items containing other items (for example, fitted ships) (lines 42–46).
- CCP’s [launch article](https://www.eveonline.com/news/view/introducing-the-hypernet-relay) says the offer item must be a single item, with no stacks, in a normal station rather than a structure (line 14). It gives unique ships, Officer modules, Abyssal modules, corpses, and BPCs as examples (line 23).
- [EVE University’s HyperNet Relay guide](https://wiki.eveuniversity.org/HyperNet_Relay) independently describes a single, unstacked item in an NPC station (lines 116–123).

The published lists are examples, not a complete type allowlist. In particular, CCP explicitly supports module prizes. Containers and items containing other items remain disallowed.

## Client validation contract

The client drop handler forwards the dragged item to the create controller (`raffles/client/window.py:151–155`; `raffles/client/create/drag_drop_area.py:144–145`). `CreationController.validate_item` (`raffles/client/create/controller.py:289–302`) checks:

1. `item.ownerID == session.charid`.
2. `util.is_type_valid_for_hypernet(item.typeID)`.
3. The singleton exception below.
4. `item.flagID == inventorycommon.const.flagHangar`.
5. `IsStation(item.locationID)`.
6. The station’s solar system is not Triglavian.

It does not compare `categoryID`, inspect container contents, or explicitly verify stack quantity in `validate_item`. The create request carries the selected item/type/location identifiers and raffle values (`controller.py:141`); the server must validate the actual inventory record, including one-item and contents constraints.

`raffles/common/util.py:51–52` defines the type gate as `is_tradable(type_id) and type_id not in GetTypeIDsByListID(VALIDATION_TYPE_LIST_ID)`. `inventoryrestrictions/common/checks.py:17–18` defines `is_tradable` as no restriction in `TYPE_LIST_NON_TRADABLE_ITEMS` or `TYPE_LIST_SOULBOUND_ITEMS`. `evetypes/const.py:14,21` maps these to typeList IDs 36 and 142. The pinned SDE records are `native/typelist.jsonl.gz` keys 36 (`NonTradableItems`, group 29 plus explicit types) and 142 (`Soulbound`, category 2118 plus explicit types, excluding groups 4725/4726). These are direct SDE typeList IDs, not a separate restrictions-table ID. `inventoryrestrictions/common/tracker.py:19–24` loads each list through `evetypes.GetTypeIDsByListID`; `:36–44` checks whether a type belongs to any requested list.

`_validate_singleton` (`controller.py:305–312`) accepts non-singletons; for singleton items it accepts dynamic types or IDs in `ALLOWED_SINGLETON_TYPE_LIST_ID`. `menucheckers/baseCheckers.py:511–512` defines `ItemChecker.IsSingleton()` as `bool(item.singleton)`. `evetypes/__init__.py:281–282` defines `IsDynamicType(typeID)` as `bool(_GetAttributeForType(typeID, 'isDynamicType'))`.

The pinned client constants are in `raffles/common/const.py:7–8`: validation list 133 and singleton allowlist 134. The local pinned SDE records are `native/typelist.jsonl.gz`:

- TypeList 133, `RaffleTypeValidation`, description “Blacklist - List of types that cannot be traded or used in raffles / the hypernet”: `includedCategoryIDs=[25,2]`, `includedGroupIDs=[943]`, `excludedGroupIDs=[14]`, `includedTypeIDs=[44992,52568]`, `excludedCategoryIDs=[]`, `excludedTypeIDs=[]`.
- TypeList 134, `RaffleSingletonValidation`, description “Whitelist - List of singleton types that can be traded or used in raffles / the hypernet”: `includedCategoryIDs=[9]` (Blueprint), `includedGroupIDs=[14]`; no excluded categories/groups/types and no explicit included types.

`evetypes/__init__.py:692–703` (`GetTypeIDsFromTypeList`) expands a type list by applying included/excluded categories, then unioning included-minus-excluded groups, then unioning included-minus-excluded explicit types. This is a layered set union: a type explicitly included by a group or type ID can still be included even if its category is excluded. CCP’s [SDE guide](https://developers.eveonline.com/docs/guides/staticdata/) documents this category/group/type expansion order. Evaluate the complete lists rather than assuming category exclusions override later included groups/types. No additional container-content check appears in the raffle client; the server must enforce CCP’s contents rule against authoritative inventory data.

## Completion notification contract

`raffles/client/storage.py:10–14,44–66` handles `OnRaffleUpdatedServer(raffle_id, raffle_data)`, `OnTicketsUpdatedServer({raffle_id: sold_ticket_count})`, `OnRaffleFinishedServer(raffle_id, winning_ticket)`, and `OnRaffleCreatedServer(raffle_id, raffle_data)`. There is no `OnRaffleExpiredServer` callback. `Raffle.update_winning_ticket(None)` sets `finished_expired` and marks the raffle finished (`raffles/client/raffle.py:80–89`), so expiry completion uses `OnRaffleFinishedServer(raffle_id, None)`; a non-null ticket sets `finished_undelivered`. `Raffle.subscribe` subscribes by raffle ID unless already subscribed or finished (`raffle.py:91–96`), while the UI's global ticket subscription is opened/closed with the raffle service window (`raffle_service.py:70–84`). Storage applies updates to cached raffles; the full-update callback carries the full model, whereas the finish callback carries only the ID and nullable winning ticket.

## Category evidence

The pinned `compat/categories.jsonl.gz` maps the relevant SDE category IDs as follows: 6 Ship, 7 Module, 9 Blueprint, 18 Drone, 20 Implant, 30 Apparel, 87 Fighter, and 91 SKINs.

A server allowlist `[9, 91, 6, 87, 18, 30]` rejects every category-7 Module, although the official CCP articles expressly name Officer and Abyssal modules as valid examples. The client’s `validate_item` has no category gate, and its type-list predicate does not categorically exclude category 7. This is a concrete false rejection. A category-20 omission alone does not establish a defect: singleton items must still satisfy the client’s dynamic/typeList134 exception, and these sources do not establish that all implants are eligible.

Blueprint category 9 appears in the singleton allowlist. The client browse contract distinguishes Blueprint Original and Blueprint Copy with `meta_data['is_copy']`; CCP patch notes also say HyperNet icons identify whether an offer item is a Blueprint Copy or Blueprint Original: [December 2019 patch notes](https://www.eveonline.com/news/view/patch-notes-for-december-2019-release), lines 185–186. Eligibility should follow the type/item checks, while `is_copy` remains metadata for representation and filtering.

## Implementation implications

Use the client’s pinned type restrictions and authoritative inventory facts. Enforce owner, station/hangar, singleton exceptions, item quantity, container/content rules, and current location server-side. Do not use a broad category list that rejects supported category-7 modules, and do not infer that every type in an included category is eligible without evaluating the full client restrictions.
