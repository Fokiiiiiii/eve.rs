ALTER TABLE space_items DROP CONSTRAINT space_items_kind_valid;
ALTER TABLE space_items ADD CONSTRAINT space_items_kind_valid CHECK (kind IN (0, 1, 2, 3));
CREATE UNIQUE INDEX space_items_moon_chunk_extraction_idx ON space_items (moon_extraction_id) WHERE kind = 3;
