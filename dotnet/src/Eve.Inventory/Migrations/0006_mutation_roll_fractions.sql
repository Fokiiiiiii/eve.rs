ALTER TABLE item_mutations
    ADD COLUMN roll_fractions jsonb;

COMMENT ON COLUMN item_mutations.roll_fractions IS 'attribute id -> the uniform [0, 1] draw used for the mutation';
