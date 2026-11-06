-- Shared (access group) folders: the four access lists of a folder, and each character's connection to a shared folder it has.
-- A shared folder's own known and active flags stay unused; the connection holds them per character.
ALTER TABLE folders ADD COLUMN admin_group_id bigint;
ALTER TABLE folders ADD COLUMN manage_group_id bigint;
ALTER TABLE folders ADD COLUMN use_group_id bigint;
ALTER TABLE folders ADD COLUMN view_group_id bigint;

CREATE TABLE connections (
    folder_id bigint NOT NULL REFERENCES folders(folder_id) ON DELETE CASCADE,
    character_id bigint NOT NULL,
    is_active boolean NOT NULL,
    PRIMARY KEY (folder_id, character_id)
);
CREATE INDEX connections_character ON connections(character_id, folder_id);
