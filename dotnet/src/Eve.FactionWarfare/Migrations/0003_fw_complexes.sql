CREATE TABLE fw_complexes (
    complex_id          bigserial PRIMARY KEY,
    solar_system_id     bigint    NOT NULL REFERENCES fw_systems (solar_system_id),
    size                smallint  NOT NULL CHECK (size BETWEEN 1 AND 4),
    progress_ms         bigint    NOT NULL DEFAULT 0,
    progress_updated_ms bigint    NOT NULL DEFAULT 0,
    spawned_ms          bigint    NOT NULL,
    closed_ms           bigint
);

-- one open complex per size in a system
CREATE UNIQUE INDEX fw_complexes_open ON fw_complexes (solar_system_id, size) WHERE closed_ms IS NULL;
