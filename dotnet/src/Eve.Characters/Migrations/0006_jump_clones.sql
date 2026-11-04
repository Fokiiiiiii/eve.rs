-- Jump clones: the bodies a character has stored (at an NPC station, in an Upwell structure, or in a ship), with the implants in each.
-- The clone limit and the cooldown come from skills; these tables hold only what is installed.
CREATE SEQUENCE jump_clone_ids AS bigint START WITH 140000000001;

CREATE TABLE character_jump_clones (
    jump_clone_id bigint      PRIMARY KEY DEFAULT nextval('jump_clone_ids'),
    character_id  bigint      NOT NULL REFERENCES characters (id),
    location_id   bigint      NOT NULL CHECK (location_id > 0),
    location_kind text        NOT NULL CHECK (location_kind IN ('station', 'structure', 'ship')),
    clone_name    text        NOT NULL DEFAULT '' CHECK (char_length(clone_name) <= 100),
    created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX character_jump_clones_character_idx ON character_jump_clones (character_id);

CREATE TABLE character_jump_clone_implants (
    jump_clone_id bigint   NOT NULL REFERENCES character_jump_clones (jump_clone_id) ON DELETE CASCADE,
    slot          smallint NOT NULL CHECK (slot >= 0),
    type_id       integer  NOT NULL CHECK (type_id > 0),
    PRIMARY KEY (jump_clone_id, slot)
);

-- the FILETIME of the last clone jump (0 when none or reset)
ALTER TABLE characters
    ADD COLUMN IF NOT EXISTS time_last_clone_jump_filetime bigint NOT NULL DEFAULT 0
        CHECK (time_last_clone_jump_filetime >= 0);
