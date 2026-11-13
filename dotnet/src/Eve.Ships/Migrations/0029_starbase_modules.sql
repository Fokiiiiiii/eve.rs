-- Anchored POS modules share the durable deployed-object owner with their control tower.
ALTER TABLE deployed ADD COLUMN control_tower_id BIGINT NOT NULL DEFAULT 0;

ALTER TABLE deployed DROP CONSTRAINT deployed_kind_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_kind_check CHECK (kind IN (
    'cyno_field', 'mobile_warp_disruptor', 'mobile_cyno_inhibitor', 'mobile_cyno_beacon',
    'analysis_beacon', 'mobile_observatory', 'control_tower', 'starbase_module',
    'dungeon_pocket', 'dungeon_gate', 'tournament.beacon'
));

CREATE INDEX deployed_control_tower_idx ON deployed (control_tower_id, deployable_id)
    WHERE kind = 'starbase_module';
