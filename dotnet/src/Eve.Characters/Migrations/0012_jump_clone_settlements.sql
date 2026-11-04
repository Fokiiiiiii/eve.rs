-- The settlement of each jump clone installation (EveJS jumpCloneInstallSettlements): the fee, the clone it made, and who was paid
-- (a structure's owner corporation, or the offering pilot of a ship). The clone row and its settlement are written in one transaction.
CREATE TABLE character_jump_clone_settlements (
    operation_id          text           PRIMARY KEY CHECK (char_length(operation_id) BETWEEN 1 AND 128),
    character_id          bigint         NOT NULL REFERENCES characters (id),
    jump_clone_id         bigint         NOT NULL CHECK (jump_clone_id > 0),
    location_id           bigint         NOT NULL CHECK (location_id > 0),
    location_kind         text           NOT NULL CHECK (location_kind IN ('station', 'structure', 'ship')),
    amount                numeric(20, 2) NOT NULL CHECK (amount >= 0),
    owner_corporation_id  bigint         NOT NULL DEFAULT 0 CHECK (owner_corporation_id >= 0),
    offering_character_id bigint         NOT NULL DEFAULT 0 CHECK (offering_character_id >= 0),
    settled_at            timestamptz    NOT NULL DEFAULT now()
);
CREATE INDEX character_jump_clone_settlements_character_idx ON character_jump_clone_settlements (character_id);
