-- The achievement tracker of each character as the client reports it (EveJS legacyTracker): the completed achievements with their completion
-- filetimes and the client event counts. A character with a header row has a record (possibly empty); one without has none.
CREATE TABLE achievement_trackers (
    character_id bigint PRIMARY KEY REFERENCES characters(id),
    updated_ms bigint NOT NULL
);

CREATE TABLE achievement_completions (
    character_id bigint NOT NULL REFERENCES achievement_trackers(character_id),
    achievement_id integer NOT NULL CHECK (achievement_id > 0),
    completed_filetime bigint NOT NULL CHECK (completed_filetime >= 0),
    PRIMARY KEY (character_id, achievement_id)
);

CREATE TABLE achievement_event_counts (
    character_id bigint NOT NULL REFERENCES achievement_trackers(character_id),
    event_name text NOT NULL CHECK (event_name <> ''),
    event_count bigint NOT NULL CHECK (event_count >= 0),
    PRIMARY KEY (character_id, event_name)
);
