-- The last modification of an order (decision Q3: the modification delay runs from the later of the issue and this time).
ALTER TABLE orders ADD COLUMN last_modified_ms bigint NOT NULL DEFAULT 0;
