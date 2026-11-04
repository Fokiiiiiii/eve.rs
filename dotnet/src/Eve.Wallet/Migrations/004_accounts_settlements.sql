ALTER TABLE balances ADD COLUMN account_key integer NOT NULL DEFAULT 1000 CHECK(account_key BETWEEN 1000 AND 1006);
ALTER TABLE journal DROP CONSTRAINT journal_owner_id_fkey;
ALTER TABLE balances DROP CONSTRAINT balances_pkey;
ALTER TABLE balances ADD PRIMARY KEY(owner_id,account_key);
ALTER TABLE balances ADD CONSTRAINT division_no_plex CHECK(account_key=1000 OR plex=0);
ALTER TABLE journal ADD COLUMN account_key integer NOT NULL DEFAULT 1000;
ALTER TABLE journal DROP CONSTRAINT journal_owner_id_operation_key_key;
ALTER TABLE journal ADD UNIQUE(owner_id,account_key,operation_key);
ALTER TABLE journal ADD FOREIGN KEY(owner_id,account_key) REFERENCES balances(owner_id,account_key);
CREATE INDEX journal_account_owner ON journal(owner_id,account_key,id DESC);
CREATE TABLE settlements (
    operation_key text PRIMARY KEY,
    context text NOT NULL,
    fingerprint text NOT NULL,
    time_ms bigint NOT NULL,
    result jsonb NOT NULL
);
