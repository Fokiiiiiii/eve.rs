-- Player control towers (ST-23): the fuel state of a control tower in its deployed record.
-- online: its fuel paid for the current hour; offline: it did not (or is not yet fuelled); unanchoring: its unanchoring has been asked and runs.
ALTER TABLE deployed ADD COLUMN tower_state TEXT NOT NULL DEFAULT 'offline';
ALTER TABLE deployed ADD CONSTRAINT deployed_tower_state_check CHECK (tower_state IN ('offline', 'online', 'unanchoring'));
