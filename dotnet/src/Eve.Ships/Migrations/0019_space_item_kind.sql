ALTER TABLE space_items
    ADD COLUMN kind integer NOT NULL DEFAULT 0;

ALTER TABLE space_items
    ADD CONSTRAINT space_items_kind_valid CHECK (kind IN (0, 1, 2));
