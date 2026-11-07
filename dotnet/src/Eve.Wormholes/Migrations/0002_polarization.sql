-- A character is polarized to the connection they just traversed, regardless of which end they entered.
CREATE TABLE wormhole_polarizations (
    character_id  bigint NOT NULL,
    connection_id bigint NOT NULL REFERENCES wormhole_connections(connection_id) ON DELETE CASCADE,
    until_ms      bigint NOT NULL,
    PRIMARY KEY (character_id, connection_id)
);
CREATE INDEX wormhole_polarizations_expiry ON wormhole_polarizations (until_ms);
