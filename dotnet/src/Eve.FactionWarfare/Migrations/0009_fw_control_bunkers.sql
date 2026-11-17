-- One persistent client-visible Control Bunker identity per occupied FW system. IDs use CCP's documented Control Bunker range.
ALTER TABLE fw_systems ADD COLUMN control_bunker_id bigint NOT NULL DEFAULT 0;
ALTER TABLE fw_systems ADD COLUMN pending_occupier_id bigint NOT NULL DEFAULT 0;
ALTER TABLE fw_systems ADD COLUMN pending_capture_ms bigint NOT NULL DEFAULT 0;
CREATE UNIQUE INDEX fw_systems_control_bunker_id_idx ON fw_systems (control_bunker_id) WHERE control_bunker_id > 0;
CREATE SEQUENCE fw_control_bunker_ids START WITH 80000000 MINVALUE 80000000 MAXVALUE 80099999 NO CYCLE;
