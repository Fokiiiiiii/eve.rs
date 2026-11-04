-- Characters: who a player is. What a character owns (ships, skills, money) lives in the modules that own it.
CREATE SEQUENCE character_ids AS bigint START WITH 140000001;

CREATE TABLE characters (
    id                bigint   PRIMARY KEY DEFAULT nextval('character_ids'),
    account_id        integer  NOT NULL,
    name              text     NOT NULL,
    -- the name folded for uniqueness (NFKC, spaces collapsed, lower case): "Ann  Lee" and "ann lee" are one name
    name_key          text     NOT NULL UNIQUE,
    gender            smallint NOT NULL CHECK (gender IN (0, 1, 2)),
    race_id           integer  NOT NULL,
    bloodline_id      integer  NOT NULL,
    ancestry_id       integer  NOT NULL,
    school_id         integer  NOT NULL,
    -- the legacy "character" type the client shows for the bloodline
    type_id           integer  NOT NULL,
    corporation_id    bigint   NOT NULL,
    faction_id        bigint,
    alliance_id       bigint,
    station_id        bigint   NOT NULL,
    home_station_id   bigint   NOT NULL,
    solar_system_id   integer  NOT NULL,
    constellation_id  integer  NOT NULL,
    region_id         integer  NOT NULL,
    -- the ship the character is in (an inventory item)
    ship_id           bigint,
    -- what the client sent for the paper doll and the portrait, kept as sent (marshal bytes)
    doll_info         bytea,
    portrait_info     bytea,
    created_at        timestamptz NOT NULL DEFAULT now(),
    last_logoff_at    timestamptz,
    delete_prepare_at timestamptz
);

CREATE INDEX characters_account_idx ON characters (account_id);
