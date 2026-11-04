-- One persisted controller per structure, character, and session.
CREATE SEQUENCE structure_pilot_versions AS bigint;

CREATE TABLE structure_pilots (
    structure_id          bigint  PRIMARY KEY REFERENCES structures (structure_id) ON DELETE CASCADE,
    character_id          bigint  NOT NULL CHECK (character_id > 0) UNIQUE,
    session_id            bigint  NOT NULL CHECK (session_id > 0) UNIQUE,
    original_ship_id      bigint  NOT NULL CHECK (original_ship_id > 0),
    original_location_id  bigint  CHECK (original_location_id IS NULL OR original_location_id > 0),
    from_space            boolean NOT NULL,
    version               bigint  NOT NULL DEFAULT 1 CHECK (version > 0)
);
