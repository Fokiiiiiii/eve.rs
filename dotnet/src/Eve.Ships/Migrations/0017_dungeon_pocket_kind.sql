-- Deadspace pockets of the dungeons (ST-23, protocol 74): the deployed record kind of a dungeon's pocket, placed in the scene at its origin.
ALTER TABLE deployed DROP CONSTRAINT deployed_kind_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_kind_check CHECK (kind IN ('cyno_field', 'mobile_warp_disruptor', 'mobile_cyno_inhibitor', 'mobile_cyno_beacon', 'analysis_beacon', 'mobile_observatory', 'control_tower', 'dungeon_pocket'));
