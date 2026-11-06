ALTER TABLE bookmarks ALTER COLUMN item_id DROP NOT NULL;
ALTER TABLE bookmarks ADD COLUMN x double precision;
ALTER TABLE bookmarks ADD COLUMN y double precision;
ALTER TABLE bookmarks ADD COLUMN z double precision;
ALTER TABLE bookmarks ADD CONSTRAINT bookmarks_coordinates_all_or_none
    CHECK ((x IS NULL AND y IS NULL AND z IS NULL) OR (x IS NOT NULL AND y IS NOT NULL AND z IS NOT NULL));
