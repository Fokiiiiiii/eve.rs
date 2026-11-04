CREATE TABLE IF NOT EXISTS project_discovery_training_issuances (
    character_id bigint NOT NULL,
    account_id bigint NOT NULL,
    level integer NOT NULL CHECK (level BETWEEN 0 AND 7),
    task_number bigint NOT NULL,
    catalog_revision text NOT NULL,
    PRIMARY KEY(character_id, level, task_number)
);
