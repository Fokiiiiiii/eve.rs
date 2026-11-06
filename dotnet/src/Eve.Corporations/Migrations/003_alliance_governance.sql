ALTER TABLE alliances ALTER COLUMN executor_corporation_id DROP NOT NULL;
ALTER TABLE alliance_members ALTER COLUMN chosen_executor_id DROP NOT NULL;
ALTER TABLE alliances ADD COLUMN suspended boolean NOT NULL DEFAULT false;
ALTER TABLE alliances ADD COLUMN dictatorial boolean NOT NULL DEFAULT false;
ALTER TABLE alliance_applications ADD COLUMN due_ms bigint;
ALTER TABLE alliance_applications ADD CONSTRAINT alliance_application_status CHECK(status BETWEEN 1 AND 4);
UPDATE alliance_applications SET due_ms=updated_ms+86400000 WHERE status=2 AND NOT archived AND due_ms IS NULL;
ALTER TABLE alliance_applications ADD CONSTRAINT alliance_application_active_acceptance_due CHECK(status<>2 OR archived OR due_ms IS NOT NULL);
CREATE INDEX alliance_applications_due ON alliance_applications(due_ms,id) WHERE status=2 AND NOT archived AND due_ms IS NOT NULL;
