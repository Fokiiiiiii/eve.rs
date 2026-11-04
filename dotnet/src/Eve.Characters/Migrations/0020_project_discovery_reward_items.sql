ALTER TABLE project_discovery_tutorial_receipts ADD COLUMN IF NOT EXISTS item_id bigint;
ALTER TABLE project_discovery_tutorial_receipts ADD COLUMN IF NOT EXISTS item_type_id integer;
ALTER TABLE project_discovery_tutorial_receipts ADD COLUMN IF NOT EXISTS delivery_station_id bigint;
