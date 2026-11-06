-- Wallets: what an owner (a character) has. Who the owner is lives in the module that owns owners.
CREATE TABLE balances (
    owner_id bigint        PRIMARY KEY,
    -- ISK, to the cent
    isk      numeric(20,2) NOT NULL,
    -- PLEX, whole units
    plex     bigint        NOT NULL CHECK (plex >= 0)
);
