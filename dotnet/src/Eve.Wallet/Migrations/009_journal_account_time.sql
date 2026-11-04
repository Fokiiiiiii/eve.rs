CREATE INDEX journal_account_owner_time ON journal(owner_id, account_key, time_ms DESC);
