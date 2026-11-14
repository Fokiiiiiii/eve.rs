CREATE TABLE account_omega_training_history (
    account_id    integer     NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
    effective_at  timestamptz NOT NULL,
    expiry_at     timestamptz NOT NULL,
    PRIMARY KEY (account_id, effective_at)
);
