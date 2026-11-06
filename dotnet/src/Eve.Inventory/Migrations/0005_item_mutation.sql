CREATE TABLE item_mutations (
    item_id         bigint   PRIMARY KEY REFERENCES items (id) ON DELETE CASCADE,
    mutator_type_id integer  NOT NULL,
    mutator_item_id bigint   NOT NULL,
    source_type_id  integer  NOT NULL,
    source_item_id  bigint   NOT NULL,
    character_id    bigint   NOT NULL,
    created_at_ms   bigint   NOT NULL,
    -- attribute id -> the multiplier that was rolled (exact double)
    rolls           jsonb    NOT NULL,
    -- attribute id -> the source type's base value of that attribute
    base_values     jsonb    NOT NULL,
    -- attribute id -> base_values x rolls (the mutated value; no rounding)
    mutated_values  jsonb    NOT NULL
);

CREATE INDEX item_mutations_character_idx ON item_mutations (character_id);
