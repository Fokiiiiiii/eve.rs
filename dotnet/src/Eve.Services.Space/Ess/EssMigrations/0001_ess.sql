-- The Encounter Surveillance System of a solar system (ESS): its Main Bank, its Reserve Bank and what was taken from them.
-- One row per ESS. The money is in ISK to the cent. Times are milliseconds since 1970, like the bounty store.
-- main_link_* is the link of the thief who is hacking the Main Bank (all four set, or all four null).
-- next_payout_ms is when the Main Bank is next paid to its owner; next_pulse_ms is when the next Reserve Bank pulse is due.
CREATE TABLE systems (
    solar_system_id integer PRIMARY KEY,
    ess_id bigint NOT NULL CHECK (ess_id > 0),
    beacon_id bigint NOT NULL,
    type_id integer NOT NULL,
    owner_id bigint,
    current_output double precision NOT NULL,
    main_value numeric(20, 2) NOT NULL DEFAULT 0 CHECK (main_value >= 0),
    reserve_value numeric(20, 2) NOT NULL DEFAULT 0 CHECK (reserve_value >= 0),
    main_link_id text,
    main_link_character_id bigint,
    main_link_started_ms bigint,
    main_link_completes_ms bigint,
    CHECK ((main_link_id IS NULL) = (main_link_character_id IS NULL) AND (main_link_id IS NULL) = (main_link_completes_ms IS NULL)),
    next_payout_ms bigint NOT NULL,
    reserve_key_type_id bigint,
    reserve_pulses_total integer NOT NULL DEFAULT 0 CHECK (reserve_pulses_total >= 0),
    reserve_pulses_remaining integer NOT NULL DEFAULT 0 CHECK (reserve_pulses_remaining >= 0 AND reserve_pulses_remaining <= reserve_pulses_total),
    reserve_opened_ms bigint,
    next_pulse_ms bigint
);
CREATE INDEX systems_due_idx ON systems (main_link_completes_ms) WHERE main_link_id IS NOT NULL;

-- The characters linked to the Reserve Bank of a system (one bank a character can hold at a time is checked by the rules, not here).
CREATE TABLE reserve_links (
    solar_system_id integer NOT NULL REFERENCES systems (solar_system_id) ON DELETE CASCADE,
    character_id bigint NOT NULL,
    linked_ms bigint NOT NULL,
    PRIMARY KEY (solar_system_id, character_id)
);

-- Who took what from a bank, and when: a Main Bank theft, or one reserve payout to a linked character.
CREATE TABLE thefts (
    id bigserial PRIMARY KEY,
    solar_system_id integer NOT NULL REFERENCES systems (solar_system_id) ON DELETE CASCADE,
    bank text NOT NULL CHECK (bank IN ('main', 'reserve')),
    character_id bigint NOT NULL,
    amount numeric(20, 2) NOT NULL CHECK (amount > 0),
    at_ms bigint NOT NULL
);
CREATE INDEX thefts_system_idx ON thefts (solar_system_id, bank, id);
