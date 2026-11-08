-- An accepted storyline mission consumes one pending offer. The receipt makes
-- the effect idempotent when the acceptance outbox is retried after a restart.
CREATE TABLE storyline_offer_receipts (
    character_id bigint NOT NULL CHECK (character_id > 0),
    acceptance_reference text NOT NULL CHECK (length(acceptance_reference) BETWEEN 1 AND 128),
    faction_id bigint NOT NULL CHECK (faction_id > 0),
    level bigint NOT NULL CHECK (level BETWEEN 1 AND 5),
    PRIMARY KEY (character_id, acceptance_reference)
);
