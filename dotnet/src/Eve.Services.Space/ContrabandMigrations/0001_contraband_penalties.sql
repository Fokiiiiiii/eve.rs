-- A customs penalty settled by the contraband.penalty-settle job (ST-18). One row per operation, written in the job's own transaction with the
-- fine, the standing loss and the confiscation: its presence makes a repeat of the job a no-op. Times are milliseconds since 1970.
CREATE TABLE contraband_penalties (
    operation_id text PRIMARY KEY,
    character_id bigint NOT NULL CHECK (character_id > 0),
    ship_id bigint NOT NULL CHECK (ship_id > 0),
    solar_system_id integer NOT NULL,
    faction_id integer NOT NULL CHECK (faction_id > 0),
    total_fine numeric(20, 2) NOT NULL CHECK (total_fine >= 0),
    charged_fine numeric(20, 2) NOT NULL CHECK (charged_fine >= 0 AND charged_fine <= total_fine),
    standing_loss double precision NOT NULL CHECK (standing_loss >= 0),
    confiscated_item_ids bigint[] NOT NULL,
    settled_ms bigint NOT NULL
);
