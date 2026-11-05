CREATE SEQUENCE order_ids AS bigint START WITH 1 MAXVALUE 99999999;
CREATE TABLE orders (
    id bigint PRIMARY KEY DEFAULT nextval('order_ids'),
    type_id integer NOT NULL CHECK(type_id > 0),
    bid boolean NOT NULL,
    price numeric(20,2) NOT NULL CHECK(price > 0 AND price <= 9223372036854),
    vol_remaining bigint NOT NULL CHECK(vol_remaining >= 0),
    vol_entered bigint NOT NULL CHECK(vol_entered > 0 AND vol_remaining <= vol_entered),
    min_volume bigint NOT NULL CHECK(min_volume > 0 AND min_volume <= vol_entered),
    range_value integer NOT NULL,
    station_id bigint NOT NULL CHECK(station_id > 0),
    solar_system_id integer NOT NULL CHECK(solar_system_id > 0),
    constellation_id integer NOT NULL CHECK(constellation_id > 0),
    region_id integer NOT NULL CHECK(region_id > 0),
    issued_ms bigint NOT NULL, duration_days integer NOT NULL CHECK(duration_days >= 0),
    is_npc boolean NOT NULL DEFAULT false,
    seed_key text UNIQUE,
    actor_id bigint, wallet_owner_id bigint, account_key integer CHECK(account_key BETWEEN 1000 AND 1006),
    escrow_item_id bigint, escrow_isk numeric(20,2) NOT NULL DEFAULT 0 CHECK(escrow_isk >= 0),
    source_owner_id bigint, source_location_id bigint, source_flag integer,
    state integer NOT NULL DEFAULT 0 CHECK(state BETWEEN 0 AND 3),
    CHECK(is_npc OR (actor_id IS NOT NULL AND wallet_owner_id IS NOT NULL AND actor_id > 0 AND wallet_owner_id > 0 AND account_key IS NOT NULL)),
    CHECK(is_npc OR duration_days IN (1,3,7,14,30,90)),
    CHECK(seed_key IS NULL OR is_npc)
);
CREATE INDEX orders_asks ON orders(region_id,type_id,price,station_id,id) WHERE NOT bid AND state=0 AND vol_remaining>0;
CREATE INDEX orders_bids ON orders(region_id,type_id,price DESC,station_id,id) WHERE bid AND state=0 AND vol_remaining>0;
CREATE INDEX orders_actor ON orders(actor_id,state,id) WHERE NOT is_npc;
CREATE INDEX orders_expiry ON orders(issued_ms,id) WHERE NOT is_npc AND state=0;
