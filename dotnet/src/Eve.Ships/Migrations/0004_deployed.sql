-- The records of the deployables the host keeps (ADR 0012): what lies in space on the host's clock, which the expiry sweep takes out when its time is
-- up and which the host puts back in space after a restart. kind names the sweep's entry for the record (cyno_field: a cynosural field).
CREATE TABLE deployed (
    deployable_id    bigint           PRIMARY KEY,
    kind             text             NOT NULL CHECK (kind IN ('cyno_field')),
    solar_system_id  integer          NOT NULL,
    name             text             NOT NULL,
    type_id          integer          NOT NULL,
    group_id         integer          NOT NULL,
    x                double precision NOT NULL,
    y                double precision NOT NULL,
    z                double precision NOT NULL,
    radius           double precision NOT NULL,
    owner_id         bigint           NOT NULL DEFAULT 0,
    corporation_id   integer          NOT NULL DEFAULT 0,
    alliance_id      integer          NOT NULL DEFAULT 0,
    ship_id          bigint           NOT NULL DEFAULT 0,
    -- when it goes (milliseconds since 1970)
    expires_at_ms    bigint           NOT NULL
);

CREATE INDEX deployed_system_idx ON deployed (solar_system_id);
CREATE INDEX deployed_expiry_idx ON deployed (expires_at_ms);
