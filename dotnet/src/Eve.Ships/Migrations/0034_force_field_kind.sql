-- A POS control tower's force field (protocol 104, SimLink kinds 180 to 183): a durable space item whose launcher is the tower.
-- A tower has at most one field item; the partial unique index makes a second one impossible, so a repeat sync cannot create one.
ALTER TABLE space_items DROP CONSTRAINT space_items_kind_valid;
ALTER TABLE space_items ADD CONSTRAINT space_items_kind_valid CHECK (kind IN (0, 1, 2, 3, 4, 5, 6));
CREATE UNIQUE INDEX space_items_force_field_tower_idx ON space_items (launcher_id) WHERE kind = 6;
