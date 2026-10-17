-- complexes opened before the dungeon sites have no site: they are closed without a capture (the respawn opens them again with a site)
DELETE FROM fw_complexes WHERE closed_ms IS NULL;
ALTER TABLE fw_complexes ADD COLUMN instance_id bigint;
