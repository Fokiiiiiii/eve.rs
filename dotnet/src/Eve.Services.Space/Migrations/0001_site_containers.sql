-- The containers of the cosmic sites (ST-10 d): the data and relic containers an analyzer opens (wiki: Data Site and Relic Site containers).
-- One row per container. state: 'secure' (hackable), 'cleared' (a win), 'destroyed' (two failed attempts). failures counts the lost attempts.
-- expires_at_ms is the end of the site (milliseconds since 1970). The row is written in the site's fill transaction and changed with its event.
CREATE TABLE site_containers (
    container_id bigint PRIMARY KEY,
    site_id bigint NOT NULL,
    solar_system_id integer NOT NULL,
    role text NOT NULL,
    relic boolean NOT NULL,
    type_id integer NOT NULL,
    group_id integer NOT NULL,
    x double precision NOT NULL,
    y double precision NOT NULL,
    z double precision NOT NULL,
    radius double precision NOT NULL,
    difficulty integer NOT NULL,
    failures integer NOT NULL DEFAULT 0,
    state text NOT NULL DEFAULT 'secure' CHECK (state IN ('secure', 'cleared', 'destroyed')),
    expires_at_ms bigint NOT NULL
);
CREATE INDEX site_containers_system_idx ON site_containers (solar_system_id) WHERE state = 'secure';
CREATE INDEX site_containers_site_idx ON site_containers (site_id);
