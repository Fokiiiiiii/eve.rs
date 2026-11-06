-- A jump drive spool in progress (the EVE University wiki, "Jump drive": the drive spools, then the ship leaves). The simulation keeps the spool in memory,
-- so the host keeps the fuel it took and the fatigue it replaced: a spool that does not complete (a restart, a pilot who is gone, a simulation that dropped it)
-- is refunded and its fatigue restored (SpaceJumpDrive.CancelInterruptedAsync and SweepAsync). One row a character: a second spool needs the first to be over.
CREATE TABLE jump_spool (
    character_id            bigint   PRIMARY KEY,
    ship_id                 bigint   NOT NULL,
    field_id                bigint   NOT NULL,
    destination_system      integer  NOT NULL,
    ends_at_ms              bigint   NOT NULL,
    fuel_type_id            integer  NOT NULL,
    fuel_quantity           bigint   NOT NULL,
    prior_fatigue_until_ms  bigint   NOT NULL,
    prior_cooldown_until_ms bigint   NOT NULL
);
