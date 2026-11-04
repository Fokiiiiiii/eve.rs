-- Personal bookmark folders of a character, their subfolders and bookmarks (the client's accessGroupBookmarkMgr).
-- Ids come from one sequence, so a folder, a subfolder and a bookmark never share an id.
CREATE SEQUENCE bookmark_ids START 1000000001;

CREATE TABLE folders (
    folder_id bigint PRIMARY KEY DEFAULT nextval('bookmark_ids'),
    owner_id bigint NOT NULL,
    folder_name text NOT NULL,
    description text NOT NULL DEFAULT '',
    is_personal boolean NOT NULL DEFAULT true,
    known boolean NOT NULL DEFAULT true,
    is_active boolean NOT NULL DEFAULT true,
    created_ms bigint NOT NULL
);
CREATE INDEX folders_owner ON folders(owner_id, folder_id);

CREATE TABLE subfolders (
    subfolder_id bigint PRIMARY KEY DEFAULT nextval('bookmark_ids'),
    folder_id bigint NOT NULL REFERENCES folders(folder_id) ON DELETE CASCADE,
    subfolder_name text NOT NULL,
    creator_id bigint NOT NULL,
    created_ms bigint NOT NULL
);
CREATE INDEX subfolders_folder ON subfolders(folder_id, subfolder_id);

CREATE TABLE bookmarks (
    bookmark_id bigint PRIMARY KEY DEFAULT nextval('bookmark_ids'),
    folder_id bigint NOT NULL REFERENCES folders(folder_id) ON DELETE CASCADE,
    subfolder_id bigint REFERENCES subfolders(subfolder_id) ON DELETE CASCADE,
    item_id bigint NOT NULL,
    type_id integer NOT NULL,
    location_id bigint NOT NULL,
    memo text NOT NULL,
    note text NOT NULL,
    creator_id bigint NOT NULL,
    created_ms bigint NOT NULL,
    expiry_ms bigint
);
CREATE INDEX bookmarks_folder ON bookmarks(folder_id, bookmark_id);
CREATE INDEX bookmarks_expiry ON bookmarks(expiry_ms) WHERE expiry_ms IS NOT NULL;
