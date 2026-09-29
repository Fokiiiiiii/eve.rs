-- How far a fitted module is switched on. A module with no row is offline; a module that leaves its slot loses its row.
CREATE TABLE module_state (
    item_id bigint   PRIMARY KEY,
    -- the item states of dogma: 0 offline, 1 online (2 active and 3 overloaded belong to space and are not kept)
    state   smallint NOT NULL CHECK (state >= 0 AND state <= 1)
);
