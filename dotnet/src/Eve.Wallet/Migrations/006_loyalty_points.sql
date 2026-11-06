-- Loyalty points: the balance a character's wallet or a corporation's wallet holds with each issuing corporation, and
-- the journal of every change. A journal row is unique per reference, holder and issuer, so a replayed award applies once.
CREATE TABLE lp_balances (
    holder_kind    text   NOT NULL CHECK (holder_kind IN ('character', 'corporation')),
    holder_id      bigint NOT NULL CHECK (holder_id > 0),
    issuer_corp_id bigint NOT NULL CHECK (issuer_corp_id > 0),
    amount         bigint NOT NULL CHECK (amount >= 0),
    PRIMARY KEY (holder_kind, holder_id, issuer_corp_id)
);

CREATE TABLE lp_journal (
    id             bigserial   PRIMARY KEY,
    holder_kind    text        NOT NULL CHECK (holder_kind IN ('character', 'corporation')),
    holder_id      bigint      NOT NULL CHECK (holder_id > 0),
    issuer_corp_id bigint      NOT NULL CHECK (issuer_corp_id > 0),
    delta          bigint      NOT NULL CHECK (delta <> 0),
    balance_after  bigint      NOT NULL CHECK (balance_after >= 0),
    reason         text        NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 64),
    reference      text        NOT NULL CHECK (char_length(reference) BETWEEN 1 AND 128),
    created_at     timestamptz NOT NULL DEFAULT now(),
    UNIQUE (reference, holder_kind, holder_id, issuer_corp_id)
);
