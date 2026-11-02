CREATE INDEX deployed_control_tower_owner_idx ON deployed (corporation_id, activate_at_ms) WHERE kind = 'control_tower';
