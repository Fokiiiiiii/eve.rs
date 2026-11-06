-- Mobile Observatories (E-04's remote decloak ping, protocol 72): the deployed record kind of an observatory.
ALTER TABLE deployed DROP CONSTRAINT deployed_kind_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_kind_check CHECK (kind IN ('cyno_field', 'mobile_warp_disruptor', 'mobile_cyno_inhibitor', 'mobile_cyno_beacon', 'analysis_beacon', 'mobile_observatory'));
