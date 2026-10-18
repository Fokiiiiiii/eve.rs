-- The character titles of each character (EveJS achievementState ownedTitleIds, equippedTitleId): the titles it owns, and the one it wears.
-- A worn title must be owned (the foreign key), so a title that is taken away takes the wearing with it. The ids are the SDE's character title keys.
CREATE TABLE character_titles (
    character_id bigint NOT NULL REFERENCES characters(id),
    title_id uuid NOT NULL,
    granted_ms bigint NOT NULL CHECK (granted_ms >= 0),
    PRIMARY KEY (character_id, title_id)
);

CREATE TABLE character_equipped_titles (
    character_id bigint PRIMARY KEY,
    title_id uuid NOT NULL,
    FOREIGN KEY (character_id, title_id) REFERENCES character_titles (character_id, title_id) ON DELETE CASCADE
);
