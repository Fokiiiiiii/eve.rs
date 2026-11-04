-- R&D research projects (agents track, AG-3 / A6): one row per character and research agent while the project runs.
-- remainder_points is what was banked when the rate last changed; the points now are remainder + rate * days since the start
-- (ESI GET /characters/{character_id}/agents_research: remainder_points + points_per_day * days). Cancelling deletes the row
-- and the points in it are lost, as the client says ("All points will be lost when the connection is broken").
CREATE TABLE research_projects (
    character_id     bigint           NOT NULL CHECK (character_id > 0),
    agent_id         bigint           NOT NULL CHECK (agent_id > 0),
    skill_type_id    bigint           NOT NULL CHECK (skill_type_id > 0),
    started_at_ms    bigint           NOT NULL,
    points_per_day   double precision NOT NULL CHECK (points_per_day >= 0),
    remainder_points double precision NOT NULL DEFAULT 0 CHECK (remainder_points >= 0),
    PRIMARY KEY (character_id, agent_id)
);
