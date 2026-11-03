ALTER TABLE project_discovery_bonus_receipts
    DROP CONSTRAINT IF EXISTS project_discovery_bonus_receipts_experience_check;
ALTER TABLE project_discovery_bonus_receipts
    ADD CONSTRAINT project_discovery_bonus_receipts_experience_check CHECK (experience >= 0);
