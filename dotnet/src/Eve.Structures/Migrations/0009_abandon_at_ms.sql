-- The sampled, absolute Low Power abandonment deadline. Existing rows retain the seven-day deadline already used by their scheduled timer.
ALTER TABLE structures ADD COLUMN abandon_at_ms bigint NOT NULL DEFAULT 0;

UPDATE structures
SET abandon_at_ms = low_power_since_ms + 604800000
WHERE upkeep = 2 AND low_power_since_ms > 0 AND destroyed_ms = 0;
