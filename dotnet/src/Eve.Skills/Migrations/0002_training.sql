-- Skill training: the queue a character has lined up, whether it runs, and what the history says happened.
-- Times are FILETIME ticks (100 ns since 1601), the unit the client reads.
CREATE TABLE skill_queue (
    character_id bigint   NOT NULL,
    position     integer  NOT NULL,
    type_id      integer  NOT NULL,
    to_level     smallint NOT NULL CHECK (to_level BETWEEN 1 AND 5),
    PRIMARY KEY (character_id, position)
);

CREATE TABLE training_state (
    character_id bigint  PRIMARY KEY,
    -- the queue is running
    active       boolean NOT NULL,
    -- when the running line began; null when nothing runs
    active_start bigint
);

CREATE TABLE skill_history (
    id              bigserial PRIMARY KEY,
    character_id    bigint   NOT NULL,
    log_date        bigint   NOT NULL,
    event_type      integer  NOT NULL,
    type_id         integer  NOT NULL,
    absolute_points bigint   NOT NULL,
    level           smallint NOT NULL
);
CREATE INDEX skill_history_by_character ON skill_history (character_id, id DESC);
