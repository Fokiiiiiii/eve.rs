-- ST-9 f: the owner's custom vulnerability window per hub (a change takes effect after the next window), the default window of an alliance that a captured hub
-- inherits, and the length locked for each window (the wiki: a day's length is locked at the midpoint of the window the day before).
CREATE TABLE hub_windows (
    hub_id bigint PRIMARY KEY,
    centre_hour smallint CHECK (centre_hour BETWEEN 0 AND 23),
    length_hours smallint CHECK (length_hours BETWEEN 1 AND 18),
    pending_centre_hour smallint CHECK (pending_centre_hour BETWEEN 0 AND 23),
    pending_length_hours smallint CHECK (pending_length_hours BETWEEN 1 AND 18),
    pending_from_ms bigint CHECK (pending_from_ms IS NULL OR pending_from_ms > 0)
);
CREATE TABLE alliance_windows (
    alliance_id bigint PRIMARY KEY,
    centre_hour smallint NOT NULL CHECK (centre_hour BETWEEN 0 AND 23),
    length_hours smallint NOT NULL CHECK (length_hours BETWEEN 1 AND 18)
);
CREATE TABLE window_locks (
    hub_id bigint NOT NULL,
    centre_ms bigint NOT NULL,
    duration_ms bigint NOT NULL CHECK (duration_ms > 0),
    PRIMARY KEY (hub_id, centre_ms)
);
