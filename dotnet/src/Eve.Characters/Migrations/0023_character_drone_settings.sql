CREATE TABLE character_drone_settings (
    character_id bigint NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
    attribute_id integer NOT NULL,
    value boolean NOT NULL,
    PRIMARY KEY (character_id, attribute_id)
);
