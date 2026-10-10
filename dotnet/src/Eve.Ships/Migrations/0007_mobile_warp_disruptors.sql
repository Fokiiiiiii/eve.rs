-- Mobile warp disruptors (WP-8, protocol 48): the deployed record of a disruptor the host keeps. activate_at_ms is when its anchoring delay is over (it is
-- anchored from then on, and its warp disruption is on); range_m is the range of its warp disruption in metres.
ALTER TABLE deployed DROP CONSTRAINT deployed_kind_check;
ALTER TABLE deployed ADD CONSTRAINT deployed_kind_check CHECK (kind IN ('cyno_field', 'mobile_warp_disruptor'));
ALTER TABLE deployed ADD COLUMN activate_at_ms bigint NOT NULL DEFAULT 0;
ALTER TABLE deployed ADD COLUMN range_m double precision NOT NULL DEFAULT 0;
