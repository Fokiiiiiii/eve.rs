-- Pirate insurgency campaigns (C-M1). One row per campaign. state holds the client's campaign constants (V24.01
-- pirateinsurgency/const.py:6-13: 2 forecasting, 3 active, 4 pirate win, 5 antipirate win, 6 no winner, 7 cleanup, 99 terminal).
-- Times are milliseconds since 1970. The campaign's structure is referenced by id only (the FOB belongs to the structures owner).
CREATE TABLE insurgency_campaigns (
    campaign_id                bigint    generated always as identity PRIMARY KEY,
    pirate_faction_id          bigint    NOT NULL,
    warzone_id                 bigint    NOT NULL,
    origin_solar_system_id     bigint    NOT NULL,
    covered_solar_system_ids   bigint[]  NOT NULL,
    structure_id               bigint    NOT NULL DEFAULT 0,
    state                      integer   NOT NULL,
    started_ms                 bigint    NOT NULL,
    max_ends_ms                bigint    NOT NULL,
    state_expires_ms           bigint    NOT NULL,
    pirate_points_required     integer   NOT NULL,
    antipirate_points_required integer   NOT NULL,
    pirate_points_scored       integer   NOT NULL DEFAULT 0,
    antipirate_points_scored   integer   NOT NULL DEFAULT 0,
    ended_ms                   bigint    NOT NULL DEFAULT 0
);
CREATE INDEX insurgency_campaigns_open_idx ON insurgency_campaigns (state) WHERE state NOT IN (1, 99);

-- The ambition modifier of each side in a warzone (0 to 5). A side needs 5 + its modifier systems to win.
CREATE TABLE insurgency_ambition (
    warzone_id          bigint  PRIMARY KEY,
    pirate_modifier     integer NOT NULL DEFAULT 0 CHECK (pirate_modifier BETWEEN 0 AND 5),
    antipirate_modifier integer NOT NULL DEFAULT 0 CHECK (antipirate_modifier BETWEEN 0 AND 5)
);
