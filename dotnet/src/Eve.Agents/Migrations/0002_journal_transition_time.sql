-- The game clock of each agent transition (the journal's created_at is the database clock). Older rows have none; the
-- completed-mission history falls back to created_at for them.
ALTER TABLE journal ADD COLUMN transition_at timestamptz;
