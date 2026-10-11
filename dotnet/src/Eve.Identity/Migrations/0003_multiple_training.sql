-- Multiple Character Training: the extra skill-training queues of an account. An account has two extra queues (slots 2 and 3),
-- each running until its end time. A slot whose end time has passed is free again; a row is kept so the last end time is known.
-- Written by the store (a certificate or a Multiple Character Training purchase) and by the activation of a certificate.
CREATE TABLE account_training_slots (
    account_id integer     NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
    slot       smallint    NOT NULL CHECK (slot IN (2, 3)),
    ends_at    timestamptz NOT NULL,
    PRIMARY KEY (account_id, slot)
);
