CREATE TABLE IF NOT EXISTS project_discovery_tutorial_claim_calls (
    character_id bigint NOT NULL,
    account_id bigint NOT NULL,
    session_id bigint NOT NULL,
    call_id bigint NOT NULL,
    granted boolean NOT NULL,
    PRIMARY KEY(character_id, session_id, call_id)
);
