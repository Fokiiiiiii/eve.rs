-- NPC bounties that are earned and not yet paid. A kill adds its bounty to the killer's bucket for the solar system it was in; the bucket is paid out in
-- one credit when its time comes (the first kill of a character starts a window, later kills before it ends join it). A bucket is deleted in the same
-- transaction that credits the wallet, so a crash pays it once.
CREATE TABLE pending_bounties (
    -- character:solar system:payout time (a FILETIME)
    key               text         PRIMARY KEY,
    character_id      bigint       NOT NULL,
    solar_system_id   integer      NOT NULL,
    payout_at_ms      bigint       NOT NULL,
    payout_time       bigint       NOT NULL,
    amount            numeric(30,2) NOT NULL CHECK (amount > 0),
    kills             integer      NOT NULL CHECK (kills > 0),
    -- how many NPCs of each type were killed: {"23245": 2}
    npc_types         jsonb        NOT NULL,
    -- the ships whose bounty is in the bucket (the first 32), so that one kill is not counted twice
    source_references bigint[]     NOT NULL
);
CREATE INDEX pending_bounties_due ON pending_bounties (payout_at_ms);
CREATE INDEX pending_bounties_character ON pending_bounties (character_id, payout_at_ms);
CREATE INDEX pending_bounties_sources ON pending_bounties USING gin (source_references);
