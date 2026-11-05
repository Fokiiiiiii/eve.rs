-- Wormhole connections (slice 4): one row per connection between two systems. A static is re-opened in its source
-- system after it collapses (wiki "Wormhole": a static collapse opens a replacement elsewhere in the system). Times are
-- milliseconds since 1970. seed is the SplitMix64 seed of the connection, stored as the signed bigint of its bits.
CREATE TABLE wormhole_connections (
    connection_id          bigint    generated always as identity PRIMARY KEY,
    source_system_id       integer   NOT NULL,
    destination_system_id  integer   NOT NULL,
    type_id                integer   NOT NULL,
    code                   text      NOT NULL,
    is_static              boolean   NOT NULL,
    mass_kg                double precision NOT NULL,
    max_stable_mass_kg     double precision NOT NULL,
    created_ms             bigint    NOT NULL,
    expires_ms             bigint    NOT NULL,
    active                 boolean   NOT NULL,
    seed                   bigint    NOT NULL
);
CREATE INDEX wormhole_connections_source ON wormhole_connections (source_system_id) WHERE active;
