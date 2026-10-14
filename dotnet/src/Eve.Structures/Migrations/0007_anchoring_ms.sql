-- Existing and newly created legacy structures retain the standard 24-hour anchor duration.
ALTER TABLE structures
    ADD COLUMN anchoring_ms bigint NOT NULL DEFAULT 86400000 CHECK (anchoring_ms > 0);
