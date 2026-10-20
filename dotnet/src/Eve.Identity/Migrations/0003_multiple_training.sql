CREATE TABLE account_training_slots (
    account_id integer     NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
    slot       smallint    NOT NULL CHECK (slot IN (2, 3)),
    ends_at    timestamptz NOT NULL,
    PRIMARY KEY (account_id, slot)
);
