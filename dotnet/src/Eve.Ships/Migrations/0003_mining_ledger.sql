-- The mining ledger (EVE.js miningLedgerState.js), one row per granted mining cycle, as a relational table. A pilot's own entries are owner_kind
-- 'character' (owner_id is the pilot); an observer's are 'observer' (owner_id is the observer's item id). Written by the subscriber mining.ledger in the
-- transaction of the subscriber, read through the rowset read side. The entry id is the event id, so a replayed event writes nothing. Entries are kept
-- for 90 days (the reference's retention); a new entry prunes the older ones.
CREATE TABLE mining_ledger_entries (
    entry_id           bigint  PRIMARY KEY,
    owner_kind         text    NOT NULL CHECK (owner_kind IN ('character', 'observer')),
    owner_id           bigint  NOT NULL,
    character_id       bigint  NOT NULL,
    corporation_id     bigint  NOT NULL,
    solar_system_id    bigint  NOT NULL,
    event_date         bigint  NOT NULL,
    visible_after      bigint  NOT NULL,
    type_id            bigint  NOT NULL,
    quantity           bigint  NOT NULL,
    quantity_wasted    bigint  NOT NULL,
    quantity_critical  bigint  NOT NULL,
    ship_type_id       bigint  NOT NULL,
    module_type_id     bigint  NOT NULL,
    observer_item_id   bigint  NOT NULL,
    yield_kind         text    NOT NULL
);
CREATE INDEX mining_ledger_entries_owner ON mining_ledger_entries (owner_kind, owner_id, event_date DESC);
CREATE INDEX mining_ledger_entries_date ON mining_ledger_entries (event_date);

-- The observers (a structure with a scanner) that have a ledger: their name and system, and the span of their entries: the first time one is shown
-- (first_visible_after, the earliest visible_after) and the newest event (last_event_date), so the observers' list filters by time in one query.
CREATE TABLE mining_ledger_observers (
    item_id            bigint  PRIMARY KEY,
    corporation_id     bigint  NOT NULL,
    solar_system_id    bigint  NOT NULL,
    item_name          text    NOT NULL,
    first_visible_after bigint NOT NULL,
    last_event_date    bigint  NOT NULL
);
