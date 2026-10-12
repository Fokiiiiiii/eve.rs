-- The ship a Main Bank link was made from: a change of ship breaks the link (the client's UNLINKED_SHIP_CHANGE).
ALTER TABLE systems ADD COLUMN main_link_ship_id bigint;
