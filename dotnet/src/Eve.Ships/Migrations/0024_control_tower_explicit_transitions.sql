-- POS control towers have separate anchoring and online transitions. Keep the
-- active phase in the existing deployed record; the durable scheduler stores
-- each deadline, while this state distinguishes unanchored/anchoring/onlining.
ALTER TABLE deployed DROP CONSTRAINT deployed_tower_state_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_tower_state_check
    CHECK (tower_state IN ('unanchored', 'anchoring', 'offline', 'onlining', 'online', 'unanchoring'));
