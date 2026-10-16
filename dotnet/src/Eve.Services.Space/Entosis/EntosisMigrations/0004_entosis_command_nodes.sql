-- ST-17 a: the command node stage after a reinforcement (the wiki). A campaign is started at the reinforcement's exit and ends when one side reaches its
-- bar; its nodes are live rows (their links are the links table's, keyed by the node id). A defended hub is invulnerable until the next window.
-- a node's links are keyed by the node id, which is no hub: the links table's foreign key to the hubs is dropped.
ALTER TABLE links DROP CONSTRAINT links_hub_id_fkey;
CREATE TABLE campaigns (
    hub_id bigint PRIMARY KEY,
    solar_system_id integer NOT NULL,
    defender_alliance_id bigint NOT NULL,
    activity_defense_multiplier double precision NOT NULL CHECK (activity_defense_multiplier >= 1),
    phase text NOT NULL CHECK (phase IN ('active', 'defended', 'destroyed')),
    started_ms bigint NOT NULL,
    ended_ms bigint,
    next_spawn_ms bigint NOT NULL,
    spawned integer NOT NULL DEFAULT 0 CHECK (spawned >= 0),
    attacker_captures integer NOT NULL DEFAULT 0 CHECK (attacker_captures >= 0),
    defender_captures integer NOT NULL DEFAULT 0 CHECK (defender_captures >= 0)
);
CREATE TABLE nodes (
    node_id bigint PRIMARY KEY,
    hub_id bigint NOT NULL REFERENCES campaigns (hub_id),
    solar_system_id integer NOT NULL,
    progress double precision NOT NULL DEFAULT 0 CHECK (progress BETWEEN -1 AND 1),
    checkpoint_ms bigint NOT NULL
);
CREATE INDEX nodes_hub_idx ON nodes (hub_id);
CREATE INDEX nodes_system_idx ON nodes (solar_system_id);
ALTER TABLE hubs ADD COLUMN invulnerable_until_ms bigint CHECK (invulnerable_until_ms IS NULL OR invulnerable_until_ms > 0);
ALTER TABLE hubs DROP CONSTRAINT hubs_phase_check;
ALTER TABLE hubs ADD CONSTRAINT hubs_phase_check CHECK (phase IN ('idle', 'capturing', 'captured', 'reinforced', 'destroyed'));
