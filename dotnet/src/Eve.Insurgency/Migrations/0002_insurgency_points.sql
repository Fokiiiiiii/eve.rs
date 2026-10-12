-- Points, contributors and sites of a campaign (slice 2). A system scores one point per side (its first completed site
-- for the pirates). A contributor is a character who took a kill in a covered system while the campaign was active; the
-- victory payout goes to each of them. A site is the dungeon a campaign started in a covered system.
CREATE TABLE insurgency_points (
    campaign_id     bigint  NOT NULL REFERENCES insurgency_campaigns (campaign_id),
    solar_system_id bigint  NOT NULL,
    side            text    NOT NULL CHECK (side IN ('pirate', 'antipirate')),
    scored_ms       bigint  NOT NULL,
    PRIMARY KEY (campaign_id, solar_system_id, side)
);

CREATE TABLE insurgency_contributors (
    campaign_id  bigint NOT NULL REFERENCES insurgency_campaigns (campaign_id),
    character_id bigint NOT NULL,
    first_ms     bigint NOT NULL,
    PRIMARY KEY (campaign_id, character_id)
);

CREATE TABLE insurgency_sites (
    campaign_id     bigint  NOT NULL REFERENCES insurgency_campaigns (campaign_id),
    solar_system_id bigint  NOT NULL,
    dungeon_id      bigint  NOT NULL,
    instance_id     bigint  NOT NULL PRIMARY KEY,
    completed_ms    bigint  NULL
);
CREATE INDEX insurgency_sites_campaign ON insurgency_sites (campaign_id) WHERE completed_ms IS NULL;
