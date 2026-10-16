-- The Project Discovery tutorial of each character (EveJS projectDiscoveryService characterStates): finished or not, and the level reached.
-- A character with no row has not started: not finished, level 0.
CREATE TABLE project_discovery_tutorials (
    character_id bigint PRIMARY KEY REFERENCES characters(id),
    finished boolean NOT NULL,
    tutorial_level integer NOT NULL CHECK (tutorial_level >= 0),
    updated_ms bigint NOT NULL
);
