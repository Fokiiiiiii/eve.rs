ALTER TABLE accounts ADD COLUMN subscription_end_at timestamptz;
UPDATE accounts SET subscription_end_at = '2100-01-01T00:00:00Z' WHERE subscription_end_at IS NULL;
ALTER TABLE accounts ALTER COLUMN subscription_end_at SET DEFAULT '2100-01-01T00:00:00Z';
ALTER TABLE accounts ALTER COLUMN subscription_end_at SET NOT NULL;
