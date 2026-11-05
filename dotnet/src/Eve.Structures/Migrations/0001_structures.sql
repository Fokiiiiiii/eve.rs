-- The Upwell structures (ST-0): one row per structure, owned by this module. The columns are what the state machine and the timers work on (state ids are the
-- client's structures/const.py STATE_*, upkeep ids UPKEEP_STATE_*; times are milliseconds since 1970, 0 = none). doc holds the rest of the row that EVE.js kept
-- in the same record (description, settings, bridge data, ...), so the directory read models keep their shape; nothing in doc duplicates a column.
CREATE TABLE structures (
    structure_id         bigint           PRIMARY KEY,
    type_id              integer          NOT NULL,
    solar_system_id      integer          NOT NULL,
    x                    double precision NOT NULL DEFAULT 0,
    y                    double precision NOT NULL DEFAULT 0,
    z                    double precision NOT NULL DEFAULT 0,
    yaw                  double precision NOT NULL DEFAULT 0,
    owner_corporation_id bigint           NOT NULL DEFAULT 0,
    alliance_id          bigint           NOT NULL DEFAULT 0,
    name                 text             NOT NULL DEFAULT '',
    bio                  text             NOT NULL DEFAULT '',
    profile_id           bigint           NOT NULL DEFAULT 0,
    -- 1 unanchored, 2 anchoring, 101 fitting, 102 onlining, 110 shield, 111 armor reinforce, 112 armor, 113 hull reinforce, 114 hull, 115 anchor, 116 deploy, 118 fob
    state                smallint         NOT NULL DEFAULT 1,
    state_started_ms     bigint           NOT NULL DEFAULT 0,
    -- the deadline of the running timer (a repair or reinforcement countdown, anchoring); 0 when none runs
    state_ends_ms        bigint           NOT NULL DEFAULT 0,
    -- the last damage that paused the repair countdown; the countdown holds for a short while after it
    timer_paused_ms      bigint           NOT NULL DEFAULT 0,
    -- 1 full power, 2 low power, 3 abandoned
    upkeep               smallint         NOT NULL DEFAULT 2,
    core_installed       boolean          NOT NULL DEFAULT false,
    -- the owner's reinforcement day (0 Monday .. 6 Sunday, 255 every day) and hour (UTC)
    reinforce_weekday    smallint         NOT NULL DEFAULT 5,
    reinforce_hour       smallint         NOT NULL DEFAULT 18,
    -- the day and hour in force when the shield fight began: they fix the dates of the armor and hull timers (-1 when no fight runs)
    fight_weekday        smallint         NOT NULL DEFAULT -1,
    fight_hour           smallint         NOT NULL DEFAULT -1,
    -- a requested change of the reinforcement time, taking effect at pending_effective_ms (-1 / 0 when none)
    pending_weekday      smallint         NOT NULL DEFAULT -1,
    pending_hour         smallint         NOT NULL DEFAULT -1,
    pending_effective_ms bigint           NOT NULL DEFAULT 0,
    unanchor_ends_ms     bigint           NOT NULL DEFAULT 0,
    low_power_since_ms   bigint           NOT NULL DEFAULT 0,
    destroyed_ms         bigint           NOT NULL DEFAULT 0,
    doc                  jsonb            NOT NULL DEFAULT '{}',
    version              bigint           NOT NULL DEFAULT 1
);
CREATE INDEX structures_system_idx ON structures (solar_system_id) WHERE destroyed_ms = 0;
CREATE INDEX structures_owner_idx ON structures (owner_corporation_id) WHERE destroyed_ms = 0;

-- The service modules of a structure: service ids are the client's (1 docking, 2 fitting, 3 offices, ... see EVE.js STRUCTURE_SERVICE_ID); state 1 online, 2 offline, 3 cleanup.
CREATE TABLE structure_services (
    structure_id bigint   NOT NULL REFERENCES structures (structure_id) ON DELETE CASCADE,
    service_id   integer  NOT NULL,
    state        smallint NOT NULL,
    PRIMARY KEY (structure_id, service_id)
);
