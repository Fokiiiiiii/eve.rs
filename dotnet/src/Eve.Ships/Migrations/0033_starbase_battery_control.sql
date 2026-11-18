-- A POS battery under a pilot's control grant (V24.01 posMgr.AssumeStructureControl / RelinquishStructureControl).
-- One row names the pilot holding the battery's grant. The row is written under the battery's deployed-row lock and goes
-- with its deployed record.
CREATE TABLE starbase_battery_control (
    deployable_id BIGINT PRIMARY KEY REFERENCES deployed (deployable_id) ON DELETE CASCADE,
    character_id BIGINT NOT NULL CHECK (character_id > 0),
    granted_at_ms BIGINT NOT NULL
);

CREATE INDEX starbase_battery_control_character_idx ON starbase_battery_control (character_id);
