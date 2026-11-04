CREATE TABLE wreck_salvage_outcomes (
    wreck_id bigint PRIMARY KEY CHECK (wreck_id > 0),
    rewards jsonb NOT NULL CHECK (jsonb_typeof(rewards) = 'array')
);

INSERT INTO wreck_salvage_outcomes (wreck_id, rewards)
SELECT item_id, '[]'::jsonb
FROM space_items
WHERE kind = 0;
