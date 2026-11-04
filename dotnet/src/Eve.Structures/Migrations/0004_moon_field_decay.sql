-- When the ore field of a fractured or decayed extraction decayed (ST-6a c; EVE.js moonMiningSpaceRuntime field lifetime, 48 h by default). 0 while it lasts.
ALTER TABLE structure_moon_extractions ADD COLUMN field_decayed_ms bigint NOT NULL DEFAULT 0;
