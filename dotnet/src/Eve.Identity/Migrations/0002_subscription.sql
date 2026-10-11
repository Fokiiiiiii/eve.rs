-- The Omega licence: each account's subscription end time. Every account is Omega (no real-money charge) until
-- 2100-01-01T00:00:00Z. New accounts are written with that end time; accounts that predate the column get it here,
-- once. An operator may set an earlier end time (the account lapses to Alpha); this migration never shortens one.
ALTER TABLE accounts ADD COLUMN subscription_end_at timestamptz;
UPDATE accounts SET subscription_end_at = '2100-01-01T00:00:00Z' WHERE subscription_end_at IS NULL;
ALTER TABLE accounts ALTER COLUMN subscription_end_at SET DEFAULT '2100-01-01T00:00:00Z';
ALTER TABLE accounts ALTER COLUMN subscription_end_at SET NOT NULL;
