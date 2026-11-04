ALTER TABLE space_items
    ADD COLUMN source_container_id bigint NOT NULL DEFAULT 0;

ALTER TABLE space_items
    ADD CONSTRAINT space_items_source_container_nonnegative
    CHECK (source_container_id >= 0);
