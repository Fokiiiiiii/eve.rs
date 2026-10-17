-- battlefields: one open battlefield per system, its three capture points (the hold timers and the holder of each) and the pilots that took part
CREATE TABLE fw_battlefields (
    battlefield_id  bigserial PRIMARY KEY,
    solar_system_id bigint    NOT NULL REFERENCES fw_systems (solar_system_id),
    occupier_id     bigint    NOT NULL,
    score           bigint    NOT NULL DEFAULT 0,
    spawned_ms      bigint    NOT NULL,
    updated_ms      bigint    NOT NULL,
    closed_ms       bigint,
    winner_id       bigint
);

-- one open battlefield per system
CREATE UNIQUE INDEX fw_battlefields_open ON fw_battlefields (solar_system_id) WHERE closed_ms IS NULL;

CREATE TABLE fw_battlefield_points (
    battlefield_id bigint   NOT NULL REFERENCES fw_battlefields (battlefield_id),
    point_index    smallint NOT NULL CHECK (point_index BETWEEN 0 AND 2),
    hold_ms        bigint   NOT NULL DEFAULT 0,
    holder_id      bigint   NOT NULL DEFAULT 0,
    PRIMARY KEY (battlefield_id, point_index)
);

CREATE TABLE fw_battlefield_pilots (
    battlefield_id bigint NOT NULL REFERENCES fw_battlefields (battlefield_id),
    character_id   bigint NOT NULL,
    faction_id     bigint NOT NULL,
    PRIMARY KEY (battlefield_id, character_id)
);
