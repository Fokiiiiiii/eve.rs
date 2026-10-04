-- What crimewatch keeps for a pilot besides its safety level: the combat timers, the one timer of the suspect or criminal flag, and the system the pilot was last in when it did something that
-- counts (where the others are told it is flagged). Times are Unix milliseconds; a timer that has passed is idle. A pilot with nothing running needs no row.
CREATE TABLE combat_state (
    character_id          bigint   PRIMARY KEY,
    weapons_until_ms      bigint   NOT NULL DEFAULT 0,
    pvp_until_ms          bigint   NOT NULL DEFAULT 0,
    npc_until_ms          bigint   NOT NULL DEFAULT 0,
    disapproval_until_ms  bigint   NOT NULL DEFAULT 0,
    flag                  smallint NOT NULL DEFAULT 0 CHECK (flag BETWEEN 0 AND 2),
    flag_until_ms         bigint   NOT NULL DEFAULT 0,
    solar_system_id       bigint   NOT NULL DEFAULT 0,
    updated_ms            bigint   NOT NULL
);

-- who is flagged where (the client asks for the flagged of its system, and is told when that changes)
CREATE INDEX combat_state_flagged ON combat_state (solar_system_id) WHERE flag <> 0;
