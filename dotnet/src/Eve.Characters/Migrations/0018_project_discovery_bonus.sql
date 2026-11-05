CREATE TABLE IF NOT EXISTS project_discovery_bonus_days (
    character_id bigint NOT NULL,
    day date NOT NULL,
    used integer NOT NULL CHECK (used >= 0),
    PRIMARY KEY(character_id, day)
);
CREATE TABLE IF NOT EXISTS project_discovery_bonus_receipts (
    character_id bigint NOT NULL,
    assignment text NOT NULL,
    day date NOT NULL,
    experience bigint NOT NULL CHECK (experience > 0),
    PRIMARY KEY(character_id, assignment)
);
