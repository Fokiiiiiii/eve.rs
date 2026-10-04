-- The ship safety level of a character (the client's three-position safety: none, partial, full). A character that has none stored is at full.
CREATE TABLE safety (
    character_id bigint   PRIMARY KEY,
    level        smallint NOT NULL CHECK (level BETWEEN 0 AND 2),
    updated_ms   bigint   NOT NULL
);
