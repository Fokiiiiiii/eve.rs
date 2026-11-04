-- The ESI system cost indices (GET /industry/systems/, public): one row per solar system and industry activity, and the
-- time of the last full fetch. The job cost multiplies the estimated item value by the index of its system and activity.
CREATE TABLE industry_cost_indices (
    solar_system_id bigint NOT NULL CHECK (solar_system_id > 0),
    activity_id integer NOT NULL CHECK (activity_id > 0),
    cost_index double precision NOT NULL CHECK (cost_index >= 0 AND cost_index <= 1),
    PRIMARY KEY (solar_system_id, activity_id)
);

CREATE TABLE industry_cost_index_state (
    id integer PRIMARY KEY CHECK (id = 1),
    fetched_ms bigint NOT NULL CHECK (fetched_ms > 0)
);
