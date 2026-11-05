ALTER TABLE owner_names ADD COLUMN is_npc boolean NOT NULL DEFAULT false;
DROP INDEX owner_names_ticker_active;
CREATE UNIQUE INDEX owner_names_ticker_active ON owner_names(ticker_key) WHERE NOT deleted AND NOT is_npc;
ALTER TABLE corporations DROP CONSTRAINT corporations_member_limit_check;
ALTER TABLE corporations ADD CONSTRAINT corporations_member_limit_check CHECK (member_limit > 0 OR (is_npc AND member_limit = -1));
