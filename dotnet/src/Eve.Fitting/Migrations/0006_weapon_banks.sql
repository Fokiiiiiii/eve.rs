CREATE TABLE weapon_bank (
    ship_id          bigint NOT NULL,
    master_module_id bigint NOT NULL,
    slave_module_id  bigint NOT NULL,
    PRIMARY KEY (ship_id, master_module_id, slave_module_id),
    UNIQUE (ship_id, slave_module_id),
    CHECK (ship_id > 0 AND master_module_id > 0 AND slave_module_id > 0 AND master_module_id <> slave_module_id)
);
