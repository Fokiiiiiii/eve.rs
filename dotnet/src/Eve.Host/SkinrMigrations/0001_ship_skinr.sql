CREATE TABLE skinr_design_drafts (
    character_id bigint NOT NULL,
    draft_id uuid NOT NULL,
    name text NOT NULL,
    line_name text NOT NULL,
    ship_type_id integer NOT NULL CHECK (ship_type_id > 0),
    layout bytea NOT NULL,
    editor_configurations bytea NOT NULL,
    skin_hex text NOT NULL CHECK (skin_hex ~ '^[0-9a-f]{64}$'),
    created_ms bigint NOT NULL,
    updated_ms bigint NOT NULL,
    PRIMARY KEY (character_id, draft_id)
);

CREATE INDEX skinr_design_drafts_by_character ON skinr_design_drafts (character_id, updated_ms, draft_id);

CREATE TABLE skinr_component_licenses (
    character_id bigint NOT NULL,
    component_id integer NOT NULL CHECK (component_id > 0),
    finite_uses bigint NOT NULL DEFAULT 0 CHECK (finite_uses >= 0),
    infinite boolean NOT NULL DEFAULT false,
    first_acquired_ms bigint NOT NULL,
    updated_ms bigint NOT NULL,
    PRIMARY KEY (character_id, component_id),
    CHECK (finite_uses > 0 OR infinite)
);

CREATE TABLE skinr_sequencing_jobs (
    job_id uuid PRIMARY KEY,
    character_id bigint NOT NULL,
    request_id uuid NOT NULL,
    request_fingerprint text NOT NULL,
    skin_hex text NOT NULL CHECK (skin_hex ~ '^[0-9a-f]{64}$'),
    name text NOT NULL,
    line_name text NOT NULL,
    ship_type_id integer NOT NULL CHECK (ship_type_id > 0),
    tier integer NOT NULL CHECK (tier > 0),
    licenses integer NOT NULL CHECK (licenses > 0),
    plex_cost bigint NOT NULL CHECK (plex_cost > 0),
    due_ms bigint NOT NULL,
    state text NOT NULL CHECK (state IN ('pending', 'completed', 'failed')),
    layout bytea NOT NULL,
    editor_configurations bytea NOT NULL,
    created_ms bigint NOT NULL,
    completed_ms bigint NULL,
    UNIQUE (character_id, request_id)
);

CREATE INDEX skinr_sequencing_jobs_by_character ON skinr_sequencing_jobs (character_id, state, created_ms, job_id);
CREATE INDEX skinr_sequencing_jobs_due ON skinr_sequencing_jobs (due_ms, job_id) WHERE state = 'pending';

CREATE TABLE skinr_ship_licenses (
    character_id bigint NOT NULL,
    skin_hex text NOT NULL CHECK (skin_hex ~ '^[0-9a-f]{64}$'),
    ship_type_id integer NOT NULL CHECK (ship_type_id > 0),
    layout bytea NOT NULL,
    editor_configurations bytea NOT NULL,
    name text NOT NULL,
    line_name text NOT NULL,
    activated boolean NOT NULL DEFAULT false,
    unactivated bigint NOT NULL DEFAULT 0 CHECK (unactivated >= 0),
    escrowed bigint NOT NULL DEFAULT 0 CHECK (escrowed >= 0),
    first_acquired_ms bigint NOT NULL,
    updated_ms bigint NOT NULL,
    PRIMARY KEY (character_id, skin_hex)
);

CREATE TABLE skinr_request_receipts (
    character_id bigint NOT NULL,
    operation text NOT NULL,
    request_id uuid NOT NULL,
    fingerprint text NOT NULL,
    result_id uuid NULL,
    created_ms bigint NOT NULL,
    PRIMARY KEY (character_id, operation, request_id)
);
