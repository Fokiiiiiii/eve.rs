-- The two-hour login milestone of each account (online milliseconds counted so far, reached, reward claimed). Per account, as EveJS keeps it.
CREATE TABLE login_milestones (
    account_id bigint PRIMARY KEY CHECK (account_id > 0),
    online_ms bigint NOT NULL CHECK (online_ms >= 0),
    achieved boolean NOT NULL,
    claimed boolean NOT NULL,
    updated_ms bigint NOT NULL
);
