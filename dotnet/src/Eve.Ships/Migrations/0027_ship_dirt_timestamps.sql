CREATE TABLE IF NOT EXISTS ship_dirt_timestamps (
    ship_id bigint PRIMARY KEY CHECK (ship_id > 0),
    dirt_filetime bigint NOT NULL CHECK (dirt_filetime > 0)
);
