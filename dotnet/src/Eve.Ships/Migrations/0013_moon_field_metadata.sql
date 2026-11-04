ALTER TABLE space_items
    ADD COLUMN moon_extraction_id bigint NOT NULL DEFAULT 0,
    ADD COLUMN moon_id bigint NOT NULL DEFAULT 0,
    ADD COLUMN moon_structure_id bigint NOT NULL DEFAULT 0,
    ADD COLUMN moon_owner_corporation_id bigint NOT NULL DEFAULT 0,
    ADD COLUMN moon_rock_ordinal integer NOT NULL DEFAULT 0,
    ADD COLUMN moon_graphic_id integer NOT NULL DEFAULT 0;

CREATE UNIQUE INDEX space_items_moon_field_rock
    ON space_items (moon_extraction_id, moon_rock_ordinal)
    WHERE moon_extraction_id > 0;

ALTER TABLE mining_ledger_observers
    ADD COLUMN moon_id bigint NOT NULL DEFAULT 0;
