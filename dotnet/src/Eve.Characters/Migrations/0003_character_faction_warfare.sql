ALTER TABLE characters
    ADD COLUMN direct_faction_id bigint,
    ADD COLUMN direct_enlistment_cooldown_filetime bigint NOT NULL DEFAULT 0
        CHECK (direct_enlistment_cooldown_filetime >= 0);
