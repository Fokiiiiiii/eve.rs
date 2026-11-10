-- The Standup drill's moon chunk explodes 25 s into the 30 s manual fracture; this durable marker prevents replaying that phase after restart.
ALTER TABLE structure_moon_extractions ADD COLUMN chunk_exploded_ms bigint NOT NULL DEFAULT 0;
