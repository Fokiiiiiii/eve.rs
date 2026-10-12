-- Storyline offer counter (EVE University, Storyline missions: one storyline mission for every 16 completed missions of the same level and
-- faction; Faction Warfare excluded): per character, faction and level, the completed regular missions and the storyline offers already made.
CREATE TABLE storyline_counters (
    character_id bigint NOT NULL CHECK (character_id > 0),
    faction_id   bigint NOT NULL CHECK (faction_id > 0),
    level        bigint NOT NULL CHECK (level BETWEEN 1 AND 5),
    completed    bigint NOT NULL DEFAULT 0 CHECK (completed >= 0),
    offered      bigint NOT NULL DEFAULT 0 CHECK (offered >= 0),
    PRIMARY KEY (character_id, faction_id, level)
);
