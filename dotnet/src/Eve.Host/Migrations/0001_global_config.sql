-- The overrides of the cluster's global configuration (machoNet.SetGlobalConfigValue). A NULL value is a deletion.
CREATE TABLE global_config (
    key        text        PRIMARY KEY CHECK (key <> ''),
    value      bytea       NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);
