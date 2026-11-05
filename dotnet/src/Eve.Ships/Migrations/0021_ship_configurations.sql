CREATE TABLE IF NOT EXISTS ship_configurations (
    ship_id bigint PRIMARY KEY CHECK (ship_id > 0),
    smb_fleet_access boolean NOT NULL DEFAULT false,
    smb_corp_access boolean NOT NULL DEFAULT false,
    fleet_hangar_fleet_access boolean NOT NULL DEFAULT false,
    fleet_hangar_corp_access boolean NOT NULL DEFAULT false
);
