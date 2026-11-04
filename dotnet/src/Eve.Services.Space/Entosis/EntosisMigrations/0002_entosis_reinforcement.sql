-- ST-9 e: the owner's prime hour (the centre of its daily vulnerability window) and the reinforcement timer of an owned hub whose capture completed. The phase
-- 'reinforced' is the owned hub's completion (a neutral hub's stays 'captured').
ALTER TABLE hubs
    ADD COLUMN prime_hour smallint NOT NULL DEFAULT 0 CHECK (prime_hour BETWEEN 0 AND 23),
    ADD COLUMN reinforcement_exit_ms bigint CHECK (reinforcement_exit_ms IS NULL OR reinforcement_exit_ms > 0);
ALTER TABLE hubs DROP CONSTRAINT hubs_phase_check;
ALTER TABLE hubs ADD CONSTRAINT hubs_phase_check CHECK (phase IN ('idle', 'capturing', 'captured', 'reinforced'));
