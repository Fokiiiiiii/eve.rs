-- The New Eden store introduction state of a character (0 unset, 1 active, 2 completed, 3 skipped). A character with no row is unset.
CREATE TABLE nes_intro_states (
    character_id bigint PRIMARY KEY REFERENCES characters(id),
    state smallint NOT NULL CHECK (state IN (0, 1, 2, 3)),
    updated_ms bigint NOT NULL
);
