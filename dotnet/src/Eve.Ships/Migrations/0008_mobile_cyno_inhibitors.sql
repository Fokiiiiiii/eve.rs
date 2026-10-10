-- Mobile Cynosural Inhibitors (E-03, protocol 50): the deployed record of an inhibitor the host keeps (kind mobile_cyno_inhibitor). It uses the columns of
-- migration 0007: activate_at_ms is when it is anchored (two minutes after the drop, the wiki), range_m its range (100 km, the wiki).
ALTER TABLE deployed DROP CONSTRAINT deployed_kind_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_kind_check CHECK (kind IN ('cyno_field', 'mobile_warp_disruptor', 'mobile_cyno_inhibitor'));
