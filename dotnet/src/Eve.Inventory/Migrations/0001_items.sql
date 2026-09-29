-- Items: everything that exists in the world and can be owned, placed and moved. S2 needs only the rows a new
-- character starts with (the ship in the station hangar); the rest of inventory grows on this table.
CREATE SEQUENCE item_ids AS bigint START WITH 1990000000;

CREATE TABLE items (
    id          bigint  PRIMARY KEY DEFAULT nextval('item_ids'),
    type_id     integer NOT NULL,
    owner_id    bigint  NOT NULL,
    -- where it is: a station, a ship, a solar system, another item
    location_id bigint  NOT NULL,
    -- the slot or hangar within the location (the client's inventory flags)
    flag        integer NOT NULL,
    quantity    bigint  NOT NULL DEFAULT 1,
    -- an assembled, unique object (a ship, a fitted module) rather than a stack
    singleton   boolean NOT NULL DEFAULT false,
    custom_name text,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX items_owner_idx ON items (owner_id);
CREATE INDEX items_location_idx ON items (location_id);
