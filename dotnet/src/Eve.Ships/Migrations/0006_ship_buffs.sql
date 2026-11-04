-- The timed buffs the host keeps for each ship (track C8): every active input of a command burst or of P.A.N.I.C., with its value and when it runs out
-- (milliseconds since 1970). A restart restores them, so a ship that logs in, undocks, jumps or enters a grid again is told the buffs it carries; the expiry
-- sweep takes out the rows whose time is up.
CREATE TABLE ship_buffs (
    ship_id        bigint           NOT NULL,
    collection_id  integer          NOT NULL,
    value          double precision NOT NULL,
    expires_at_ms  double precision NOT NULL
);

CREATE INDEX ship_buffs_ship_idx ON ship_buffs (ship_id);
CREATE INDEX ship_buffs_expiry_idx ON ship_buffs (expires_at_ms);
