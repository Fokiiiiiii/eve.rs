CREATE TABLE journal (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    owner_id bigint NOT NULL REFERENCES balances(owner_id),
    counterparty_id bigint NOT NULL,
    ref_type_id integer NOT NULL,
    amount numeric(30,2) NOT NULL CHECK (amount <> 0),
    balance numeric(30,2) NOT NULL,
    reason text NOT NULL,
    time_ms bigint NOT NULL,
    operation_key text NOT NULL CHECK (length(operation_key) > 0),
    UNIQUE (owner_id, operation_key)
);
CREATE INDEX journal_owner_id ON journal (owner_id, id DESC);
