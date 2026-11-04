-- The official adjusted and average prices of every type (ESI /markets/prices/): the input of the tax and the job cost that CCP
-- defines on them. One row per type, and the time of the last full fetch.
CREATE TABLE adjusted_prices (
    type_id integer PRIMARY KEY CHECK (type_id > 0),
    adjusted_price double precision NOT NULL CHECK (adjusted_price >= 0),
    average_price double precision CHECK (average_price IS NULL OR average_price >= 0)
);

CREATE TABLE adjusted_price_state (
    id integer PRIMARY KEY CHECK (id = 1),
    fetched_ms bigint NOT NULL CHECK (fetched_ms > 0)
);
