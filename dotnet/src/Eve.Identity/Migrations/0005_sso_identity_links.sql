CREATE TABLE external_identity_links (
    issuer     text    NOT NULL CHECK (issuer <> '' AND char_length(issuer) <= 512),
    subject    text    NOT NULL CHECK (subject ~ '^CHARACTER:EVE:[1-9][0-9]{0,19}$'),
    account_id integer NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    linked_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (issuer, subject)
);

CREATE INDEX external_identity_links_account_id_idx ON external_identity_links (account_id);
