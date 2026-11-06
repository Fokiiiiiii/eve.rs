-- Content: anything a player (or a place) takes part in that has objectives and may run out of time: an agent mission, an anomaly, an incursion, a tutorial step, an
-- expedition. An instance is started from a definition; its objectives are advanced by domain events; when the objectives are met it completes, when the time or the
-- player gives out it fails, expires or is abandoned. What happens then is an event and a list of actions, so rewards and consequences belong to the features that own them.
CREATE TABLE instances (
    id          bigserial PRIMARY KEY,
    -- "mission", "anomaly", "incursion": the family; the definition is the template inside it
    kind        text   NOT NULL,
    definition  text   NOT NULL,
    -- the character, corporation, solar system or fleet the content belongs to
    owner       bigint NOT NULL,
    state       text   NOT NULL DEFAULT 'active' CHECK (state IN ('active', 'completed', 'failed', 'expired', 'abandoned')),
    -- 'sequential': the objectives are met in order; 'parallel': all at once
    order_mode  text   NOT NULL CHECK (order_mode IN ('sequential', 'parallel')),
    -- the feature's own facts about this instance (the agent, the loot table, the spawn); the runtime does not read it
    data        jsonb  NOT NULL DEFAULT '{}',
    -- at most one active instance per (kind, unique_key): "spawn this anomaly in this system once"
    unique_key  text,
    -- what to do when it ends, copied from the definition so that a running instance is not changed by a new release
    on_complete jsonb  NOT NULL DEFAULT '[]',
    on_fail     jsonb  NOT NULL DEFAULT '[]',
    created_ms  bigint NOT NULL,
    expires_ms  bigint,
    finished_ms bigint,
    result      text
);
CREATE UNIQUE INDEX instances_unique_key ON instances (kind, unique_key) WHERE state = 'active' AND unique_key IS NOT NULL;
CREATE INDEX instances_owner ON instances (kind, owner) WHERE state = 'active';

CREATE TABLE objectives (
    instance_id  bigint  NOT NULL REFERENCES instances (id) ON DELETE CASCADE,
    idx          integer NOT NULL,
    id           text    NOT NULL,
    type         text    NOT NULL,
    params       jsonb   NOT NULL DEFAULT '{}',
    -- the event topic that advances it, and which field of the event names the owner it concerns (and that owner's id): how an event finds the objectives it touches
    topic        text,
    scope_field  text    NOT NULL DEFAULT 'characterId',
    scope_id     bigint  NOT NULL,
    target       double precision NOT NULL DEFAULT 1 CHECK (target > 0),
    required     boolean NOT NULL DEFAULT true,
    progress     double precision NOT NULL DEFAULT 0,
    state        text    NOT NULL CHECK (state IN ('waiting', 'active', 'done', 'skipped', 'failed')),
    activated_ms bigint,
    done_ms      bigint,
    PRIMARY KEY (instance_id, idx)
);
CREATE INDEX objectives_by_topic ON objectives (topic, scope_id) WHERE state = 'active';
