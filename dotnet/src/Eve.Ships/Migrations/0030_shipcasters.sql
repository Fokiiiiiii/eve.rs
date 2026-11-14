CREATE TABLE IF NOT EXISTS shipcaster_network_state (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    published_at_ms bigint CHECK (published_at_ms IS NULL OR published_at_ms > 0)
);

INSERT INTO shipcaster_network_state (singleton, published_at_ms)
VALUES (true, NULL)
ON CONFLICT (singleton) DO NOTHING;

CREATE TABLE IF NOT EXISTS shipcaster_landing_pads (
    item_id bigint PRIMARY KEY CHECK (item_id > 0),
    solar_system_id integer NOT NULL CHECK (solar_system_id > 0),
    faction_id integer NOT NULL CHECK (faction_id > 0),
    is_built boolean NOT NULL,
    link_timestamp bigint CHECK (link_timestamp IS NULL OR link_timestamp > 0),
    is_disrupted boolean NOT NULL,
    CHECK (link_timestamp IS NULL OR is_built)
);

CREATE TABLE IF NOT EXISTS shipcaster_launchers (
    item_id bigint PRIMARY KEY CHECK (item_id > 0),
    solar_system_id integer NOT NULL CHECK (solar_system_id > 0),
    faction_id integer NOT NULL CHECK (faction_id > 0),
    extra_linked_faction_ids integer[] NOT NULL DEFAULT '{}',
    is_starter boolean NOT NULL,
    destination_solar_system_id integer,
    destination_landing_pad_id bigint,
    CHECK ((destination_solar_system_id IS NULL) = (destination_landing_pad_id IS NULL)),
    CHECK (destination_solar_system_id IS NULL OR destination_solar_system_id > 0),
    CHECK (destination_landing_pad_id IS NULL OR destination_landing_pad_id > 0),
    CHECK (is_starter OR destination_solar_system_id IS NULL),
    CHECK (NOT is_starter OR destination_solar_system_id IS NOT NULL)
);

CREATE TABLE IF NOT EXISTS shipcaster_target_schedule (
    launcher_id bigint NOT NULL REFERENCES shipcaster_launchers(item_id) ON DELETE CASCADE,
    target_slot smallint NOT NULL CHECK (target_slot BETWEEN 0 AND 2),
    solar_system_id integer NOT NULL CHECK (solar_system_id > 0),
    landing_pad_id bigint NOT NULL CHECK (landing_pad_id > 0),
    faction_id integer NOT NULL CHECK (faction_id > 0),
    starts_at_filetime bigint NOT NULL CHECK (starts_at_filetime > 0),
    ends_at_filetime bigint NOT NULL CHECK (ends_at_filetime > starts_at_filetime),
    PRIMARY KEY (launcher_id, target_slot)
);

CREATE INDEX IF NOT EXISTS shipcaster_target_schedule_active
    ON shipcaster_target_schedule (launcher_id, starts_at_filetime, ends_at_filetime);

CREATE TABLE IF NOT EXISTS shipcaster_starter_jumps (
    character_id bigint PRIMARY KEY CHECK (character_id > 0),
    launcher_item_id bigint NOT NULL CHECK (launcher_item_id > 0),
    destination_solar_system_id integer NOT NULL CHECK (destination_solar_system_id > 0),
    destination_landing_pad_id bigint NOT NULL CHECK (destination_landing_pad_id > 0),
    state smallint NOT NULL CHECK (state IN (0, 1)),
    claimed_at_ms bigint NOT NULL CHECK (claimed_at_ms > 0),
    used_at_ms bigint CHECK (used_at_ms IS NULL OR used_at_ms > 0),
    CHECK ((state = 0 AND used_at_ms IS NULL) OR (state = 1 AND used_at_ms IS NOT NULL))
);
