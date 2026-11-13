-- Player-owned starbase configuration is part of the existing deployed tower state (ADR 0012).
ALTER TABLE deployed ADD COLUMN tower_config jsonb NOT NULL DEFAULT '{}'::jsonb;
