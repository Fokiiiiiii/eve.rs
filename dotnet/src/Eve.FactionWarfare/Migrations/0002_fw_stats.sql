-- Factional warfare statistics (migration 0002). One row per pilot or corporation and UTC day (day = unix milliseconds / 86,400,000).
-- The producers add kills and victory points to today's row; the statistics read yesterday, the seven days before today, and all days.
CREATE TABLE fw_pilot_daily (
    character_id     bigint   NOT NULL,
    day              integer  NOT NULL,
    kills            integer  NOT NULL DEFAULT 0 CHECK (kills >= 0),
    victory_points   bigint   NOT NULL DEFAULT 0 CHECK (victory_points >= 0),
    PRIMARY KEY (character_id, day)
);

CREATE TABLE fw_corporation_daily (
    corporation_id   bigint   NOT NULL,
    day              integer  NOT NULL,
    kills            integer  NOT NULL DEFAULT 0 CHECK (kills >= 0),
    victory_points   bigint   NOT NULL DEFAULT 0 CHECK (victory_points >= 0),
    PRIMARY KEY (corporation_id, day)
);
