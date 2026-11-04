CREATE TABLE jobs (
    id             bigserial PRIMARY KEY,
    -- which handler runs it ("npc.bounty.payout"); a handler registers under one kind
    kind           text      NOT NULL,
    -- what the job is about ("ship:90000123"): at most one WAITING job per (kind, key), so a timer for the same thing is replaced, not doubled
    key            text,
    payload        jsonb     NOT NULL DEFAULT '{}',
    -- when it is due, in milliseconds since the Unix epoch (the server's clock, injected)
    run_at_ms      bigint    NOT NULL,
    state          text      NOT NULL DEFAULT 'pending' CHECK (state IN ('pending', 'running', 'failed')),
    attempts       integer   NOT NULL DEFAULT 0,
    max_attempts   integer   NOT NULL DEFAULT 5 CHECK (max_attempts > 0),
    -- a recurring job: after a run it is due again this many milliseconds after the time it was due (missed runs are skipped, not replayed)
    every_ms       bigint    CHECK (every_ms IS NULL OR every_ms > 0),
    -- who runs it and until when the claim holds; a claim that lapsed (the process died) is taken over
    lease_owner    text,
    lease_until_ms bigint,
    last_error     text,
    created_ms     bigint    NOT NULL,
    updated_ms     bigint    NOT NULL
);
CREATE UNIQUE INDEX jobs_one_waiting_per_key ON jobs (kind, key) WHERE key IS NOT NULL AND state = 'pending';
CREATE INDEX jobs_due ON jobs (run_at_ms) WHERE state = 'pending';
CREATE INDEX jobs_leases ON jobs (lease_until_ms) WHERE state = 'running';
CREATE INDEX jobs_kind ON jobs (kind, state);
