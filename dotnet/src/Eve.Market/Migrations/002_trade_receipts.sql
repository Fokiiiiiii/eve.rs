CREATE TABLE commands (
    operation_key text PRIMARY KEY, fingerprint text NOT NULL, time_ms bigint NOT NULL, result jsonb NOT NULL
);
CREATE TABLE fills (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id bigint NOT NULL REFERENCES orders(id), type_id integer NOT NULL,
    buyer_id bigint NOT NULL, seller_id bigint NOT NULL,
    buyer_wallet bigint NOT NULL, seller_wallet bigint NOT NULL,
    buyer_account integer NOT NULL, seller_account integer NOT NULL,
    station_id bigint NOT NULL, region_id integer NOT NULL,
    quantity bigint NOT NULL CHECK(quantity > 0),
    price numeric(20,2) NOT NULL CHECK(price > 0),
    gross numeric(30,2) NOT NULL CHECK(gross > 0),
    tax numeric(30,2) NOT NULL CHECK(tax >= 0), time_ms bigint NOT NULL,
    operation_key text NOT NULL, fill_index integer NOT NULL,
    UNIQUE(operation_key,fill_index)
);
CREATE INDEX fills_history ON fills(region_id,type_id,time_ms,id);
CREATE INDEX fills_buyer ON fills(buyer_wallet,buyer_account,time_ms DESC,id DESC);
CREATE INDEX fills_seller ON fills(seller_wallet,seller_account,time_ms DESC,id DESC);
