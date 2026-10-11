-- When a service came online (milliseconds since 1970; 0 when it is offline, or was online before this record existed). The cyno jammer's
-- five-minute warm-up counts from it (ST-7; SDE 35914 description: "a 5 minute warmup period"; EveJS getCynoJammerOnlineSimTime).
ALTER TABLE structure_services ADD COLUMN online_since_ms bigint NOT NULL DEFAULT 0;
