-- A donation's Host RPC operation is stable across retransmission, unlike donated_ms.
ALTER TABLE fw_lp_donations ADD COLUMN operation_id text;
ALTER TABLE fw_lp_donations ADD COLUMN donated_lp bigint;
ALTER TABLE fw_lp_donations ADD COLUMN pool_after bigint;

CREATE UNIQUE INDEX fw_lp_donations_operation_idx ON fw_lp_donations (operation_id) WHERE operation_id IS NOT NULL;
