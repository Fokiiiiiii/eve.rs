-- Persist the corporation observed with each battlefield participant so its victory-point share survives the later tick.
ALTER TABLE fw_battlefield_pilots ADD COLUMN corporation_id bigint NOT NULL DEFAULT 0;
