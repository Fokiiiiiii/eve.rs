ALTER TABLE relation_history
    ADD COLUMN old_attrs jsonb,
    ADD COLUMN new_attrs jsonb,
    ADD COLUMN old_until_ms bigint,
    ADD COLUMN new_until_ms bigint;

UPDATE relation_history
SET new_attrs = attrs;
