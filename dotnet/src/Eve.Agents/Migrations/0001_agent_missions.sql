-- Agent missions (agents track, AG-2 / A5): one row per character and agent holds the mission the agent has offered or
-- the character has accepted, with its client state (eve-logic appConst.py:1617-1624: 0 allocated, 1 offered, 2 accepted,
-- 3 failed, 4 completed, 5 declined, 6 quit, 7 cantReplay). The journal has one row per applied transition; a reference
-- names one transition, so a replayed request applies once.
CREATE TABLE missions (
    character_id       bigint      NOT NULL CHECK (character_id > 0),
    agent_id           bigint      NOT NULL CHECK (agent_id > 0),
    state              smallint    NOT NULL DEFAULT 0 CHECK (state BETWEEN 0 AND 7),
    mission_id         bigint      NOT NULL DEFAULT 0 CHECK (mission_id >= 0),
    name_id            bigint      NOT NULL DEFAULT 0 CHECK (name_id >= 0),
    title              text        NOT NULL DEFAULT '',
    mission_type_label text        NOT NULL DEFAULT '',
    important          boolean     NOT NULL DEFAULT false,
    offered_at         timestamptz,
    expires_at         timestamptz,
    accepted_at        timestamptz,
    completed_at       timestamptz,
    decline_until      timestamptz,
    quit_at            timestamptz,
    PRIMARY KEY (character_id, agent_id)
);

CREATE TABLE journal (
    id           bigserial   PRIMARY KEY,
    character_id bigint      NOT NULL CHECK (character_id > 0),
    agent_id     bigint      NOT NULL CHECK (agent_id > 0),
    mission_id   bigint      NOT NULL DEFAULT 0,
    from_state   smallint    NOT NULL CHECK (from_state BETWEEN 0 AND 7),
    to_state     smallint    NOT NULL CHECK (to_state BETWEEN 0 AND 7),
    action       text        NOT NULL CHECK (char_length(action) BETWEEN 1 AND 32),
    reference    text        NOT NULL CHECK (char_length(reference) BETWEEN 1 AND 128),
    created_at   timestamptz NOT NULL DEFAULT now(),
    UNIQUE (reference, character_id, agent_id)
);
