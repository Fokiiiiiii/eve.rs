-- The jump drive's fatigue and activation cooldown of a character (EVE University wiki, "Jump fatigue"): both run in real time, whether the pilot is logged in
-- or not, so each is kept as the Unix millisecond it ends at (0 when none is running).
CREATE TABLE jump_fatigue (
    character_id      bigint           PRIMARY KEY,
    fatigue_until_ms  bigint           NOT NULL DEFAULT 0,
    cooldown_until_ms bigint           NOT NULL DEFAULT 0
);
