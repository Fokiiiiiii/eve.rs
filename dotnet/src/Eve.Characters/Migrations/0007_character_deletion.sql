-- Character deletion: a deleted character keeps its row (its references stay valid) and is hidden from its account.
ALTER TABLE characters ADD COLUMN IF NOT EXISTS deleted_at timestamptz;
