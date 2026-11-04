-- Local participation is distinct from scientific correctness and from tutorial skip.
ALTER TABLE project_discovery_tutorials ADD COLUMN IF NOT EXISTS participation_steps integer NOT NULL DEFAULT 0;
ALTER TABLE project_discovery_tutorials ADD COLUMN IF NOT EXISTS participation_skipped boolean NOT NULL DEFAULT false;
CREATE TABLE IF NOT EXISTS project_discovery_progress (
    character_id bigint PRIMARY KEY,
    experience bigint NOT NULL DEFAULT 0 CHECK (experience >= 0)
);
CREATE TABLE IF NOT EXISTS project_discovery_tutorial_receipts (
    character_id bigint PRIMARY KEY,
    policy text NOT NULL,
    experience bigint NOT NULL,
    isk numeric(20,2) NOT NULL,
    created_ms bigint NOT NULL
);
