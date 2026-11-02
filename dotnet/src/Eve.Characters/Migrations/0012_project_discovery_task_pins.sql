-- A character-scoped developer task pin used by Project Discovery task selection.
CREATE TABLE project_discovery_task_pins (
    character_id bigint PRIMARY KEY REFERENCES characters(id),
    task_id bigint NOT NULL CHECK (task_id > 0),
    updated_ms bigint NOT NULL
);
