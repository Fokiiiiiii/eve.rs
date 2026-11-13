ALTER TABLE project_discovery_progress
    ADD COLUMN IF NOT EXISTS analysis_kredits bigint NOT NULL DEFAULT 0 CHECK (analysis_kredits >= 0);
