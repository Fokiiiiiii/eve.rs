-- Factional warfare state (migration 0001, schema factionwarfare). Times are milliseconds since 1970.

-- One row per occupied system of the two warzones. lp_pool is the system's LP pool (the upgrade level reads it); attacker_points are the
-- victory points of the occupier's enemy in this system (the contest state reads them; FwRules.Contest).
CREATE TABLE fw_systems (
    solar_system_id  bigint  PRIMARY KEY,
    warzone_id       bigint  NOT NULL,
    occupier_id      bigint  NOT NULL,
    lp_pool          bigint  NOT NULL DEFAULT 0 CHECK (lp_pool >= 0),
    attacker_points  bigint  NOT NULL DEFAULT 0 CHECK (attacker_points >= 0),
    updated_ms       bigint  NOT NULL DEFAULT 0
);
CREATE INDEX fw_systems_warzone_idx ON fw_systems (warzone_id);

-- Every LP donation to a system (the audit trail; fw_systems.lp_pool is the running sum of these amounts).
CREATE TABLE fw_lp_donations (
    donation_id      bigint  generated always as identity PRIMARY KEY,
    solar_system_id  bigint  NOT NULL,
    character_id     bigint  NOT NULL,
    corporation_id   bigint  NOT NULL,
    faction_id       bigint  NOT NULL,
    amount           bigint  NOT NULL CHECK (amount > 0),
    donated_ms       bigint  NOT NULL
);
CREATE INDEX fw_lp_donations_system_idx ON fw_lp_donations (solar_system_id);

-- The faction enlistment of a corporation (owner_kind 1) or an alliance (2). faction_id is the enlisted faction (0: none). A join or leave
-- requested now becomes next_faction_id at effective_ms, the first downtime after the request (0: no change is pending).
CREATE TABLE fw_enlistments (
    owner_kind       smallint NOT NULL CHECK (owner_kind IN (1, 2)),
    owner_id         bigint   NOT NULL,
    faction_id       bigint   NOT NULL DEFAULT 0,
    next_faction_id  bigint   NOT NULL DEFAULT 0,
    effective_ms     bigint   NOT NULL DEFAULT 0,
    PRIMARY KEY (owner_kind, owner_id)
);
