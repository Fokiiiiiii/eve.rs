-- Skills: what a character has learned. The character itself lives in the characters module.
CREATE TABLE character_skills (
    character_id bigint   NOT NULL,
    type_id      integer  NOT NULL,
    level        smallint NOT NULL CHECK (level BETWEEN 0 AND 5),
    -- the skill points the character has in this skill
    skill_points bigint   NOT NULL CHECK (skill_points >= 0),
    PRIMARY KEY (character_id, type_id)
);
