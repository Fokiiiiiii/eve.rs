-- Idempotent publication receipts survive normal outbox retention.
CREATE TABLE publication_receipts (
    topic text NOT NULL,
    publication_key text NOT NULL,
    event_id bigint NOT NULL,
    PRIMARY KEY (topic, publication_key)
);
