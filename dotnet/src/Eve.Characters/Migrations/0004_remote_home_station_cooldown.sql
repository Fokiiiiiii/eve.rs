ALTER TABLE characters
    ADD COLUMN IF NOT EXISTS next_remote_home_station_change_filetime bigint NOT NULL DEFAULT 0
        CHECK (next_remote_home_station_change_filetime >= 0);
