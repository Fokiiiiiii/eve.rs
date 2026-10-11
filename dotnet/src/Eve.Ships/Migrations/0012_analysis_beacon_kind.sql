-- CONCORD Rogue Analysis Beacons (SDE group 4137, protocol 61): the deployed record of a beacon the host keeps (kind analysis_beacon). It uses the
-- columns of migration 0007: activate_at_ms is when the beacon becomes active (its 20-second activation), range_m is 0 (a beacon is not a warp source).
ALTER TABLE deployed DROP CONSTRAINT deployed_kind_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_kind_check CHECK (kind IN ('cyno_field', 'mobile_warp_disruptor', 'mobile_cyno_inhibitor', 'mobile_cyno_beacon', 'analysis_beacon'));
