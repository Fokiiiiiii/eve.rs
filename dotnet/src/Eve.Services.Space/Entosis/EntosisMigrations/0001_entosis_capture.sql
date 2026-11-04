-- The capture of a Sovereignty Hub by entosis links (ST-9 c). One row per hub that has been contested: the attacker's progress (0 to 1) at the time it was checked,
-- the activity multiplier that fixed its capture time, the owner's alliance (null for a neutral hub) and the phase.
-- Times are milliseconds since 1970, as the other space stores keep them.
CREATE TABLE hubs (
    hub_id bigint PRIMARY KEY CHECK (hub_id > 0),
    solar_system_id integer NOT NULL,
    progress double precision NOT NULL DEFAULT 0 CHECK (progress >= 0 AND progress <= 1),
    checkpoint_ms bigint NOT NULL,
    activity_defense_multiplier double precision NOT NULL CHECK (activity_defense_multiplier >= 1 AND activity_defense_multiplier <= 6),
    defender_alliance_id bigint CHECK (defender_alliance_id IS NULL OR defender_alliance_id > 0),
    phase text NOT NULL CHECK (phase IN ('idle', 'capturing', 'captured')),
    -- the handover of a captured hub: pending (to the capturing alliance), claimed, superseded (another alliance claimed it first) or owned (an owned hub: its
    -- reinforcement and the command-node event are not built, so nothing changes hands); NULL until the capture completes
    handover text CHECK (handover IS NULL OR handover IN ('pending', 'claimed', 'superseded', 'owned')),
    captured_by_corporation_id bigint CHECK (captured_by_corporation_id IS NULL OR captured_by_corporation_id > 0),
    captured_by_alliance_id bigint CHECK (captured_by_alliance_id IS NULL OR captured_by_alliance_id > 0)
);

-- The links of a hub: a module that has settled a cycle for it. Its last settled cycle tells whether it still counts.
CREATE TABLE links (
    module_id bigint PRIMARY KEY CHECK (module_id > 0),
    hub_id bigint NOT NULL REFERENCES hubs (hub_id) ON DELETE CASCADE,
    character_id bigint NOT NULL,
    corporation_id bigint NOT NULL CHECK (corporation_id > 0),
    alliance_id bigint NOT NULL CHECK (alliance_id > 0),
    side text NOT NULL CHECK (side IN ('attacker', 'defender')),
    linked_ms bigint NOT NULL,
    last_cycle_ms bigint NOT NULL
);
CREATE INDEX links_hub_idx ON links (hub_id);
