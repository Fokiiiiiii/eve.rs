-- The scanner probes a pilot launched and keeps the record of (EVE.js probeRuntimeState): what it is, whose, where it lies, its range step and state, and when
-- it goes (its explosion delay, or one hour). The simulation keeps a ball for each probe while it is in space; the host recalls it or its time is up.
CREATE SEQUENCE scan_probe_seq START 1;

CREATE TABLE scan_probes (
    probe_id          bigint           PRIMARY KEY,
    character_id      bigint           NOT NULL,
    solar_system_id   integer          NOT NULL,
    type_id           integer          NOT NULL,
    group_id          integer          NOT NULL,
    launcher_id       bigint           NOT NULL DEFAULT 0,
    ship_id           bigint           NOT NULL DEFAULT 0,
    x                 double precision NOT NULL,
    y                 double precision NOT NULL,
    z                 double precision NOT NULL,
    dest_x            double precision NOT NULL,
    dest_y            double precision NOT NULL,
    dest_z            double precision NOT NULL,
    scan_range        double precision NOT NULL DEFAULT 0,
    range_step        integer          NOT NULL DEFAULT 7,
    state             integer          NOT NULL DEFAULT 1,
    -- when it goes (milliseconds since 1970)
    expires_at_ms     bigint           NOT NULL
);

CREATE INDEX scan_probes_character_idx ON scan_probes (character_id, solar_system_id);
CREATE INDEX scan_probes_expiry_idx ON scan_probes (expires_at_ms);
