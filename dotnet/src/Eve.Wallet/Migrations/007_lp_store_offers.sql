-- LP store offers (agents track, AG-2 / A9): the offers a corporation's store sells, as the official ESI route
-- (GET /loyalty/stores/{corporation_id}/offers) returns them, with their required items. ESI returns no standing condition
-- for an offer, so none is stored (DEVIATIONS "Agents slice AG-2, A9"). lp_store_refresh holds when a corporation's offers
-- were last fetched, which the daily cache reads.
CREATE TABLE lp_store_offers (
    corporation_id bigint NOT NULL CHECK (corporation_id > 0),
    offer_id       bigint NOT NULL CHECK (offer_id > 0),
    type_id        bigint NOT NULL CHECK (type_id > 0),
    quantity       bigint NOT NULL CHECK (quantity > 0),
    lp_cost        bigint NOT NULL CHECK (lp_cost >= 0),
    isk_cost       bigint NOT NULL CHECK (isk_cost >= 0),
    ak_cost        bigint NOT NULL DEFAULT 0 CHECK (ak_cost >= 0),
    PRIMARY KEY (corporation_id, offer_id)
);

CREATE TABLE lp_store_offer_requirements (
    corporation_id bigint NOT NULL,
    offer_id       bigint NOT NULL,
    type_id        bigint NOT NULL CHECK (type_id > 0),
    quantity       bigint NOT NULL CHECK (quantity > 0),
    PRIMARY KEY (corporation_id, offer_id, type_id),
    FOREIGN KEY (corporation_id, offer_id) REFERENCES lp_store_offers (corporation_id, offer_id) ON DELETE CASCADE
);

CREATE TABLE lp_store_refresh (
    corporation_id bigint      PRIMARY KEY CHECK (corporation_id > 0),
    fetched_at     timestamptz NOT NULL,
    offer_count    integer     NOT NULL CHECK (offer_count >= 0)
);
