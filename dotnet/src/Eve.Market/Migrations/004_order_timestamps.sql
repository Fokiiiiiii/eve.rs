ALTER TABLE orders ADD COLUMN last_state_change_ms bigint NOT NULL DEFAULT 0;
CREATE INDEX orders_corp_history ON orders(wallet_owner_id,issued_ms DESC,id DESC) WHERE NOT is_npc;
CREATE INDEX fills_type_history ON fills(type_id,time_ms);
