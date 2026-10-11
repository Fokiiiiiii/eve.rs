-- Mobile Cynosural Beacons (SDE group 4093, protocol 58): the deployed record of a beacon the host keeps (kind mobile_cyno_beacon). It uses the columns of
-- migration 0007: activate_at_ms is when it is anchored (its activation time, EveJS), range_m is 0 (a field, not a warp source).
ALTER TABLE deployed DROP CONSTRAINT deployed_kind_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_kind_check CHECK (kind IN ('cyno_field', 'mobile_warp_disruptor', 'mobile_cyno_inhibitor', 'mobile_cyno_beacon'));
