-- The last work cycle the host settled for each owner (a drone, or a module): a mining laser's or a salvager's cycle that the simulation sends and parks on
-- (ADR 0013). The simulation re-sends a cycle it has not had an answer to, so the host answers a repeat of this cycle from here and never settles it twice,
-- even after a restart. One cycle at a time per owner is in flight, so the last one is the only one that can come back.
CREATE TABLE space_work_cycles (
    owner_id         bigint      PRIMARY KEY,
    cycle_id         bigint      NOT NULL,
    accepted         boolean     NOT NULL,
    reason           text        NOT NULL,
    units_remaining  bigint      NOT NULL,
    delivered_units  bigint      NOT NULL,
    complete         boolean     NOT NULL
);
