-- Acceleration gates of the dungeon rooms (protocol 75): the deployed record kind of a gate, placed in the scene in its room.
ALTER TABLE deployed DROP CONSTRAINT deployed_kind_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_kind_check CHECK (kind IN ('cyno_field', 'mobile_warp_disruptor', 'mobile_cyno_inhibitor', 'mobile_cyno_beacon', 'analysis_beacon', 'mobile_observatory', 'control_tower', 'dungeon_pocket', 'dungeon_gate'));
