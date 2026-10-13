-- Planetary interaction (PI). One row per colony: the whole colony as a JSON document (pins, links, routes, level, the
-- colony's time, the next route id), replaced in the same transaction as the items and ISK it changes. A colony is
-- read and written as one unit, so the document is the record. Pins are item rows of the inventory store.
CREATE TABLE colonies (
    owner_id    bigint  NOT NULL,
    planet_id   bigint  NOT NULL,
    doc         jsonb   NOT NULL,
    updated_ms  bigint  NOT NULL,
    PRIMARY KEY (owner_id, planet_id)
);

-- Launches (planetMgr.GetMyLaunchesDetails). A launch is a record until its owner deletes it or its time is up.
CREATE TABLE launches (
    launch_id       bigint    generated always as identity PRIMARY KEY,
    owner_id        bigint    NOT NULL,
    planet_id       bigint    NOT NULL,
    solar_system_id bigint    NOT NULL,
    item_id         bigint    NOT NULL,
    status          boolean   NOT NULL,
    launch_time     bigint    NOT NULL,
    expires_ticks   bigint    NOT NULL,
    x               double precision NOT NULL,
    y               double precision NOT NULL,
    z               double precision NOT NULL,
    contents        jsonb     NOT NULL,
    deleted         boolean   NOT NULL DEFAULT false
);
CREATE INDEX launches_owner ON launches (owner_id) WHERE NOT deleted;
