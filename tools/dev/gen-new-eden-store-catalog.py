#!/usr/bin/env python3
"""Builds dotnet/src/Eve.Services.Core/Data/new-eden-store.v1.json from EVE.js's newEdenStore seed table.

Usage: gen-new-eden-store-catalog.py <EVE.js>/tools/DatabaseCreator/staticTables/newEdenStore/data.json

The seed is the owner-approved data source for the store catalog (the same exception as the dungeon and mission
catalogs). The output keeps what the server serves and grants:
  store 4      the in-game store (PLX prices) with its 15 seeded offers;
  store 9      the real-money offers (EVE.js sources of kind official-store) whose grants eve.rs can make;
  fastCheckout the nine USD PLEX packs with their listed prices;
  publicOffers the offers those stores reference, with their fulfillment.
Offers whose grants need a system eve.rs does not have (skill points) are left out.

Two products are corrected from the seed, against the pinned SDE (sde/lock.json build 3396210):
  omega_30_days:    typeId 0 -> 29143 "Clone Grade Omega"
  mct_slot_30_days: typeId 0 -> 63188 "Multiple Pilot Training"
Real-money PLEX offers list the PLEX item (typeId 44992 "PLEX") as their product.
"""
import hashlib
import json
import sys
from pathlib import Path

OUT = Path(__file__).resolve().parents[2] / "dotnet/src/Eve.Services.Core/Data/new-eden-store.v1.json"
SUPPORTED_KINDS = {"omega", "mct", "item", "grant_plex", "bundle"}
PLEX_TYPE_ID = 44992
OMEGA_TYPE_ID = 29143
MCT_TYPE_ID = 63188
REAL_MONEY_STORE_ID = 9
REAL_MONEY_OFFER_BASE = 9300000
REAL_MONEY_CATEGORY_OMEGA = 9000001
REAL_MONEY_CATEGORY_PACKS = 9000008

def supported(fulfillment):
    if not isinstance(fulfillment, dict) or fulfillment.get("kind") not in SUPPORTED_KINDS:
        return False
    if fulfillment["kind"] == "bundle":
        return all(supported(grant) for grant in fulfillment.get("grants", []))
    return True

def grant_type_ids(fulfillment):
    kind = fulfillment.get("kind")
    if kind == "item":
        return [fulfillment["typeID"]]
    if kind == "omega":
        return [OMEGA_TYPE_ID]
    if kind == "mct":
        return [MCT_TYPE_ID]
    if kind == "grant_plex":
        return [PLEX_TYPE_ID]
    if kind == "bundle":
        return [t for grant in fulfillment["grants"] for t in grant_type_ids(grant)]
    return []

def main():
    raw = Path(sys.argv[1]).read_bytes()
    seed = json.loads(raw)

    store4 = json.loads(json.dumps(seed["stores"]["4"]))
    for offer in store4["offers"]:
        for product in offer["products"]:
            if offer["storeOfferID"] == "omega_30_days":
                product["typeId"] = OMEGA_TYPE_ID
            if offer["storeOfferID"] == "mct_slot_30_days":
                product["typeId"] = MCT_TYPE_ID
            product.pop("imageUrl", None)
        offer.pop("thirdpartyinfo", None)
        offer.pop("label", None)
        offer.pop("href", None)
        offer.pop("imageUrl", None)

    fast_offers = []
    usd_price_by_store_offer = {}
    for offer in seed["fastCheckout"]["offers"]:
        usd_price_by_store_offer[offer["storeOfferID"]] = {"currency": offer["currency"], "price": offer["price"]}
        fast_offers.append({
            "id": offer["id"],
            "storeOfferID": offer["storeOfferID"],
            "name": offer["name"],
            "price": offer["price"],
            "currency": offer["currency"],
            "quantity": offer["quantity"],
            "baseQuantity": offer["baseQuantity"],
            "tags": offer["tags"],
            "imageUrl": offer.get("imageUrl"),
        })

    public = {}
    for key, value in seed["publicOffers"].items():
        if not supported(value.get("fulfillment")):
            continue
        public[key] = {
            "storeOfferID": value["storeOfferID"],
            "name": value["name"],
            "description": value["description"],
            "tags": value["tags"],
            "imageUrl": value["imageUrl"],
            "plexPriceInCents": value["plexPriceInCents"],
            "source": value["source"]["kind"],
            "fulfillment": value["fulfillment"],
        }

    real_offers = []
    for key, value in public.items():
        if value["source"] != "official-store":
            continue
        fulfillment = value["fulfillment"]
        products = []
        for type_id in dict.fromkeys(grant_type_ids(fulfillment)):
            quantity = fulfillment.get("plexAmount", 1) if fulfillment["kind"] == "grant_plex" else 1
            products.append({
                "id": 9100500 + len(real_offers) * 10 + len(products),
                "typeId": type_id,
                "quantity": quantity,
                "productName": value["name"],
            })
        pricing = usd_price_by_store_offer.get(key)
        real_offers.append({
            "id": REAL_MONEY_OFFER_BASE + len(real_offers),
            "storeOfferID": value["storeOfferID"],
            "name": value["name"],
            "description": value["description"],
            "offerPricings": [{"currency": pricing["currency"], "price": pricing["price"]}] if pricing else [],
            "imageUrl": value["imageUrl"],
            "products": products,
            "categories": [{"id": REAL_MONEY_CATEGORY_OMEGA if fulfillment["kind"] in ("omega", "bundle")
                            and fulfillment["kind"] == "omega" else REAL_MONEY_CATEGORY_PACKS}],
            "canPurchase": True,
        })

    for key in [k for k, v in public.items() if v["source"] != "official-store"]:
        public[key].pop("source")
    for value in public.values():
        value.pop("source", None)

    catalog = {
        "meta": {
            "schema": "eve.rs.new-eden-store.v1",
            "source": "EVE.js v0.12.9 tools/DatabaseCreator/staticTables/newEdenStore/data.json (owner-approved data source)",
            "sourceSha256": hashlib.sha256(raw).hexdigest(),
            "generator": "tools/dev/gen-new-eden-store-catalog.py",
            "sdeBuild": "3396210",
        },
        "config": {
            "enabled": seed["config"]["enabled"],
            "fastCheckoutEnabled": seed["config"]["fastCheckoutEnabled"],
            "fakeFastCheckoutResponse": "OK",
            "fakeChinaFunnelEnabled": False,
            "fakeBuyPlexOfferUrl": "",
            "useShellExecuteToBuyPlexOffer": True,
            "centsPerPlex": seed["config"]["centsPerPlex"],
        },
        "stores": {
            "4": {"storeID": 4, "name": store4["name"], "categories": store4["categories"],
                  "products": store4["products"], "offers": store4["offers"]},
            str(REAL_MONEY_STORE_ID): {
                "storeID": REAL_MONEY_STORE_ID,
                "name": "New Eden Store (real money)",
                "categories": store4["categories"],
                "products": [],
                "offers": real_offers,
            },
        },
        "publicOffers": public,
        "fastCheckout": {"offers": fast_offers},
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(catalog, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes): store4 {len(store4['offers'])} offers, "
          f"store9 {len(real_offers)} offers, {len(public)} public offers, {len(fast_offers)} fast-checkout offers")

if __name__ == "__main__":
    main()
