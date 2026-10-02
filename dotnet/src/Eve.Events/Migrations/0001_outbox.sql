-- The transactional outbox: a domain event is a row written in the same transaction as the change it announces, so the event exists exactly when the change does. Subscribers
-- read the rows in order, each with a cursor of its own; nothing is "sent", so nothing can be lost between a commit and a message.
CREATE TABLE outbox (
    id          bigserial PRIMARY KEY,
    -- the transaction that wrote the row. Row ids are handed out in the order the transactions ASK for them but become visible in the order they COMMIT, so an id alone is not
    -- a safe cursor; (tx_id, id) is, because a reader only looks at rows whose transaction is older than every transaction still running (see EventBus)
    tx_id       bigint    NOT NULL DEFAULT (pg_current_xact_id()::text::bigint),
    topic       text      NOT NULL,
    -- what the event is about ("ship:90000123"); events of one key are delivered in the order they were published (like all events)
    key         text,
    payload     jsonb     NOT NULL DEFAULT '{}',
    occurred_ms bigint    NOT NULL
);
CREATE INDEX outbox_order ON outbox (tx_id, id);
CREATE INDEX outbox_age ON outbox (occurred_ms);

-- One row per subscriber: how far it has read, and how its current event is going.
CREATE TABLE subscribers (
    name        text      PRIMARY KEY,
    cursor_tx   bigint    NOT NULL,
    cursor_id   bigint    NOT NULL,
    failures    integer   NOT NULL DEFAULT 0,
    retry_at_ms bigint,
    last_error  text,
    updated_ms  bigint    NOT NULL
);

-- An event a subscriber could not handle after all its attempts: kept for a person to look at, and skipped so the subscriber can go on.
CREATE TABLE dead_letters (
    id          bigserial PRIMARY KEY,
    subscriber  text      NOT NULL,
    event_id    bigint    NOT NULL,
    topic       text      NOT NULL,
    key         text,
    payload     jsonb     NOT NULL,
    occurred_ms bigint    NOT NULL,
    error       text      NOT NULL,
    failed_ms   bigint    NOT NULL
);
CREATE INDEX dead_letters_subscriber ON dead_letters (subscriber, id);
