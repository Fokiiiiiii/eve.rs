ALTER TABLE deployed DROP CONSTRAINT deployed_kind_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_kind_check CHECK (kind IN (
    'cyno_field', 'mobile_warp_disruptor', 'mobile_cyno_inhibitor', 'mobile_cyno_beacon',
    'analysis_beacon', 'mobile_observatory', 'control_tower', 'starbase_module',
    'dungeon_pocket', 'dungeon_gate', 'tournament.beacon', 'mobile_depot'
));
