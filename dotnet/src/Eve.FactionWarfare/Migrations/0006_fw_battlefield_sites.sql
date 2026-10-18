-- battlefields opened before their sites have no gate: they are removed (the next tick opens them with a site)
DELETE FROM fw_battlefield_pilots WHERE battlefield_id IN (SELECT battlefield_id FROM fw_battlefields WHERE closed_ms IS NULL);
DELETE FROM fw_battlefield_points WHERE battlefield_id IN (SELECT battlefield_id FROM fw_battlefields WHERE closed_ms IS NULL);
DELETE FROM fw_battlefields WHERE closed_ms IS NULL;
ALTER TABLE fw_battlefields ADD COLUMN instance_id bigint NOT NULL DEFAULT 0;
