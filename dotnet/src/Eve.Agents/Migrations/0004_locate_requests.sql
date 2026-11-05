-- Locator requests (agents track, AG-4 / A7): the character the pilot asked the locator agent to find (target_character_id, set
-- while the answer is open) and when the agent last located for the pilot (located_at_ms, the cooldown's start).
CREATE TABLE locate_requests (
    character_id        bigint NOT NULL CHECK (character_id > 0),
    agent_id            bigint NOT NULL CHECK (agent_id > 0),
    target_character_id bigint CHECK (target_character_id > 0),
    located_at_ms       bigint,
    PRIMARY KEY (character_id, agent_id)
);
