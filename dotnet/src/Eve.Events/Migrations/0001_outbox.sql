CREATE TABLE outbox (
    -- ids are handed out in the order transactions ASK for them but become visible in the order they COMMIT; a reader therefore takes only the rows up to a horizon at which
    -- no publishing transaction is open (publishers hold a shared advisory lock until they commit, a reader takes it exclusively for a moment: see EventBus)
    id          bigserial PRIMARY KEY,
    topic       text      NOT NULL,
    -- what the event is about ("ship:90000123"); events of one key are delivered in the order they were published (like all events)
    key         text,
    payload     jsonb     NOT NULL DEFAULT '{}',
    occurred_ms bigint    NOT NULL
);
CREATE INDEX outbox_age ON outbox (occurred_ms);

-- One row per subscriber: how far it has read, and how its current event is going.
CREATE TABLE subscribers (
    name        text      PRIMARY KEY,
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
