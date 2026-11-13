-- Client-visible orbital anchoring, onlining, and unanchoring survives a host restart.
CREATE TABLE orbital_states (
    item_id bigint PRIMARY KEY,
    solar_system_id integer NOT NULL,
    state smallint NOT NULL,
    state_started_at_ms bigint,
    state_ends_at_ms bigint
);
