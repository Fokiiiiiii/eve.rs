ALTER TABLE staged_items ADD COLUMN origin_owner_id bigint;
UPDATE staged_items SET origin_owner_id = owner_id;
ALTER TABLE staged_items ALTER COLUMN origin_owner_id SET NOT NULL;
ALTER TABLE staged_items ADD CONSTRAINT staged_items_origin_owner_positive CHECK (origin_owner_id > 0);
