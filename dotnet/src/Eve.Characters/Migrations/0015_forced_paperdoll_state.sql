ALTER TABLE characters
    DROP CONSTRAINT characters_paperdoll_state_check;

ALTER TABLE characters
    ADD CONSTRAINT characters_paperdoll_state_check CHECK (paperdoll_state IN (1, 3, 4));
