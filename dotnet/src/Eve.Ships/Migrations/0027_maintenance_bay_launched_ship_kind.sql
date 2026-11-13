ALTER TABLE space_items DROP CONSTRAINT space_items_kind_valid;
ALTER TABLE space_items ADD CONSTRAINT space_items_kind_valid CHECK (kind IN (0, 1, 2, 3, 4));
