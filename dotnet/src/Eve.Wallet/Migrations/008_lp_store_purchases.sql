-- LP store purchases (agents track, AG-3 / A9): one row per committed purchase of an offer. The purchase id is the
-- operation key of its journal rows (the loyalty point journal reference and the ISK journal operation key), so the
-- purchase is journalled once. payer_corporation_id is set when the corporation's loyalty points and ISK paid
-- (TakeOfferForCorporation); the required items and the granted items always belong to the character.
CREATE TABLE lp_store_purchases (
    purchase_id          text        PRIMARY KEY,
    character_id         bigint      NOT NULL CHECK (character_id > 0),
    payer_corporation_id bigint      CHECK (payer_corporation_id > 0),
    account_key          integer     NOT NULL DEFAULT 1000 CHECK (account_key BETWEEN 1000 AND 1006),
    store_corporation_id bigint      NOT NULL CHECK (store_corporation_id > 0),
    offer_id             bigint      NOT NULL CHECK (offer_id > 0),
    number_of_offers     bigint      NOT NULL CHECK (number_of_offers > 0),
    lp_cost              bigint      NOT NULL CHECK (lp_cost >= 0),
    isk_cost             numeric(20,2) NOT NULL CHECK (isk_cost >= 0),
    location_id          bigint      NOT NULL CHECK (location_id > 0),
    required_items       jsonb       NOT NULL,
    granted_items        jsonb       NOT NULL,
    created_at_ms        bigint      NOT NULL
);

CREATE INDEX lp_store_purchases_character ON lp_store_purchases (character_id, created_at_ms);
