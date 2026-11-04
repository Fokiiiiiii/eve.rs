-- What lies in space that is an item of its own: the cans pilots jettison cargo into and the wrecks of ships that were destroyed. The item and what is in it are in the
-- inventory's items; here is what only space knows (where it lies and how, whose it is, when it goes), so that the simulation can be given them again when a scene is made.
CREATE TABLE space_items (
    item_id          bigint           PRIMARY KEY,
    solar_system_id  integer          NOT NULL,
    x                double precision NOT NULL,
    y                double precision NOT NULL,
    z                double precision NOT NULL,
    dx               double precision NOT NULL,
    dy               double precision NOT NULL,
    dz               double precision NOT NULL,
    radius           double precision NOT NULL,
    -- the group of the item as the scene tells it (12 can, 186 wreck, 14 corpse)
    group_id         integer          NOT NULL,
    -- the ship that was jettisoned from or destroyed
    launcher_id      bigint           NOT NULL DEFAULT 0,
    -- who may take what is in it
    character_id     bigint           NOT NULL DEFAULT 0,
    corporation_id   bigint           NOT NULL DEFAULT 0,
    alliance_id      bigint           NOT NULL DEFAULT 0,
    war_faction_id   bigint           NOT NULL DEFAULT 0,
    -- when it goes (milliseconds since 1970)
    expires_at_ms    bigint           NOT NULL
);

CREATE INDEX space_items_system_idx ON space_items (solar_system_id);
CREATE INDEX space_items_expiry_idx ON space_items (expires_at_ms);
