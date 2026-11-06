CREATE TABLE bounty_kill_sources (
    victim_ship_id bigint PRIMARY KEY CHECK (victim_ship_id > 0),
    payout_time    bigint NOT NULL
);
INSERT INTO bounty_kill_sources (victim_ship_id, payout_time)
SELECT DISTINCT ON (source.victim_ship_id) source.victim_ship_id, bounty.payout_time
FROM pending_bounties AS bounty
CROSS JOIN LATERAL unnest(bounty.source_references) AS source(victim_ship_id)
ORDER BY source.victim_ship_id, bounty.payout_at_ms;

CREATE TABLE dynamic_bounty_systems (
    solar_system_id integer PRIMARY KEY,
    activated_at_ms  bigint NOT NULL,
    current_output   numeric(15,12) NOT NULL CHECK (current_output BETWEEN 1 AND 2),
    period_index     bigint NOT NULL CHECK (period_index >= 0),
    next_at_ms       bigint NOT NULL
);
CREATE TABLE dynamic_bounty_periods (
    solar_system_id integer NOT NULL REFERENCES dynamic_bounty_systems (solar_system_id),
    period_index    bigint NOT NULL CHECK (period_index >= 0),
    paid_cents      bigint NOT NULL DEFAULT 0 CHECK (paid_cents >= 0),
    loss_cents      bigint NOT NULL DEFAULT 0 CHECK (loss_cents >= 0),
    PRIMARY KEY (solar_system_id, period_index)
);
CREATE TABLE dynamic_bounty_losses (
    loss_id         bigint PRIMARY KEY CHECK (loss_id > 0),
    solar_system_id integer NOT NULL REFERENCES dynamic_bounty_systems (solar_system_id),
    period_index    bigint NOT NULL CHECK (period_index >= 0),
    loss_cents      bigint NOT NULL CHECK (loss_cents > 0),
    recorded_at_ms  bigint NOT NULL
);
