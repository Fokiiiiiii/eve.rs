-- Immediate matching may leave a bid smaller than the requested minimum. It can then fill only in full.
ALTER TABLE orders DROP CONSTRAINT orders_check1;
ALTER TABLE orders ADD CONSTRAINT orders_min_volume_check CHECK(min_volume > 0);
