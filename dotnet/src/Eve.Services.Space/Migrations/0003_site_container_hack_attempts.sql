-- A container has at most one analyzer attempt in flight across Host requests.
-- The receipt and its loot are committed with the container settlement so retrying a lost reply cannot grant a second reward.
CREATE TABLE site_container_hack_attempts (
    reservation_id uuid PRIMARY KEY,
    container_id bigint NOT NULL REFERENCES site_containers(container_id) ON DELETE CASCADE,
    character_id bigint NOT NULL,
    session_id bigint NOT NULL,
    status text NOT NULL CHECK (status IN ('active', 'won', 'lost', 'cancelled')),
    contents jsonb NOT NULL DEFAULT '[]'::jsonb,
    started_at_ms bigint NOT NULL,
    completed_at_ms bigint
);

CREATE UNIQUE INDEX site_container_hack_attempts_active_container_idx
    ON site_container_hack_attempts (container_id)
    WHERE status = 'active';

CREATE INDEX site_container_hack_attempts_container_idx
    ON site_container_hack_attempts (container_id, started_at_ms);
