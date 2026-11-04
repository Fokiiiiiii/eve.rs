CREATE SEQUENCE trade_ids START WITH 9000000000000000 INCREMENT BY 1;

-- One row per direct trade. Money is the ISK each side offers (trader order: initiator, target).
CREATE TABLE sessions (
    trade_id bigint PRIMARY KEY CHECK (trade_id > 0),
    location_id bigint NOT NULL CHECK (location_id > 0),
    solar_system_id bigint NOT NULL CHECK (solar_system_id >= 0),
    initiator_id bigint NOT NULL CHECK (initiator_id > 0),
    target_id bigint NOT NULL CHECK (target_id > 0 AND target_id <> initiator_id),
    status text NOT NULL CHECK (status IN ('open', 'completed', 'aborted')),
    initiator_isk numeric(20, 2) NOT NULL DEFAULT 0 CHECK (initiator_isk >= 0),
    target_isk numeric(20, 2) NOT NULL DEFAULT 0 CHECK (target_isk >= 0),
    initiator_accepted boolean NOT NULL DEFAULT false,
    target_accepted boolean NOT NULL DEFAULT false,
    created_ms bigint NOT NULL,
    closed_ms bigint
);

-- A pilot is in at most one open trade: the primary key is the rule.
CREATE TABLE participants (
    character_id bigint PRIMARY KEY,
    trade_id bigint NOT NULL REFERENCES sessions(trade_id) ON DELETE CASCADE
);

-- Items in custody. The item itself is moved into the trade container (its location), so the
-- origin is what an abort puts back. source_item_id is the stack this one was split from, if any.
CREATE TABLE staged_items (
    item_id bigint PRIMARY KEY,
    trade_id bigint NOT NULL REFERENCES sessions(trade_id) ON DELETE CASCADE,
    owner_id bigint NOT NULL CHECK (owner_id > 0),
    source_item_id bigint,
    origin_location_id bigint NOT NULL CHECK (origin_location_id > 0),
    origin_flag integer NOT NULL CHECK (origin_flag >= 0)
);
CREATE INDEX staged_items_by_trade ON staged_items(trade_id);
