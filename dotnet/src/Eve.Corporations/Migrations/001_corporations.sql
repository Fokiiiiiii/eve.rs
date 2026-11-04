CREATE SEQUENCE corporation_ids START WITH 98000000;
CREATE SEQUENCE alliance_ids START WITH 99000000;
CREATE TABLE owner_names (
    owner_id bigint PRIMARY KEY,
    name_key text NOT NULL,
    ticker_key text NOT NULL,
    deleted boolean NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX owner_names_name_active ON owner_names(name_key) WHERE NOT deleted;
CREATE UNIQUE INDEX owner_names_ticker_active ON owner_names(ticker_key) WHERE NOT deleted;
CREATE TABLE corporations (
    id bigint PRIMARY KEY REFERENCES owner_names(owner_id),
    name text NOT NULL, ticker text NOT NULL, ceo_id bigint NOT NULL, creator_id bigint NOT NULL,
    alliance_id bigint, station_id bigint NOT NULL, solar_system_id integer NOT NULL,
    tax_rate numeric(20,10) NOT NULL, lp_tax_rate numeric(20,10) NOT NULL,
    description text NOT NULL, url text NOT NULL, member_limit integer NOT NULL CHECK (member_limit > 0),
    is_npc boolean NOT NULL DEFAULT false, friendly_fire boolean NOT NULL DEFAULT false,
    created_ms bigint NOT NULL, settings jsonb NOT NULL DEFAULT '{}',
    founding_key text UNIQUE, deleted boolean NOT NULL DEFAULT false
);
CREATE INDEX corporations_alliance ON corporations(alliance_id) WHERE alliance_id IS NOT NULL;
CREATE TABLE members (
    character_id bigint PRIMARY KEY, corporation_id bigint NOT NULL REFERENCES corporations(id),
    started_ms bigint NOT NULL, title text NOT NULL DEFAULT '',
    roles bigint NOT NULL DEFAULT 0 CHECK (roles >= 0), grantable_roles bigint NOT NULL DEFAULT 0 CHECK (grantable_roles >= 0),
    roles_hq bigint NOT NULL DEFAULT 0 CHECK (roles_hq >= 0), grantable_hq bigint NOT NULL DEFAULT 0 CHECK (grantable_hq >= 0),
    roles_base bigint NOT NULL DEFAULT 0 CHECK (roles_base >= 0), grantable_base bigint NOT NULL DEFAULT 0 CHECK (grantable_base >= 0),
    roles_other bigint NOT NULL DEFAULT 0 CHECK (roles_other >= 0), grantable_other bigint NOT NULL DEFAULT 0 CHECK (grantable_other >= 0),
    base_id bigint, title_mask integer NOT NULL DEFAULT 0 CHECK (title_mask BETWEEN 0 AND 65535),
    block_roles boolean NOT NULL DEFAULT false, account_key integer NOT NULL DEFAULT 1000 CHECK (account_key BETWEEN 1000 AND 1006),
    division_id integer NOT NULL DEFAULT 0, squadron_id integer NOT NULL DEFAULT 0, role_stasis_ms bigint
);
CREATE INDEX members_corporation ON members(corporation_id,character_id);
CREATE TABLE employment (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, character_id bigint NOT NULL,
    corporation_id bigint NOT NULL, started_ms bigint NOT NULL, ended_ms bigint, deleted boolean NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX employment_active ON employment(character_id) WHERE ended_ms IS NULL;
CREATE INDEX employment_character ON employment(character_id, started_ms DESC, id DESC);
CREATE TABLE applications (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, corporation_id bigint NOT NULL REFERENCES corporations(id),
    character_id bigint NOT NULL, text text NOT NULL, status integer NOT NULL CHECK (status BETWEEN 0 AND 8),
    created_ms bigint NOT NULL, updated_ms bigint NOT NULL, response text NOT NULL DEFAULT '', archived boolean NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX applications_active ON applications(corporation_id, character_id) WHERE NOT archived;
CREATE INDEX applications_character ON applications(character_id,archived,id);
CREATE TABLE titles (
    corporation_id bigint NOT NULL REFERENCES corporations(id), id integer NOT NULL CHECK (id BETWEEN 1 AND 32768 AND (id & (id-1)) = 0),
    name text NOT NULL, roles bigint NOT NULL DEFAULT 0, grantable_roles bigint NOT NULL DEFAULT 0,
    roles_hq bigint NOT NULL DEFAULT 0, grantable_hq bigint NOT NULL DEFAULT 0,
    roles_base bigint NOT NULL DEFAULT 0, grantable_base bigint NOT NULL DEFAULT 0,
    roles_other bigint NOT NULL DEFAULT 0, grantable_other bigint NOT NULL DEFAULT 0,
    PRIMARY KEY(corporation_id,id)
);
CREATE TABLE alliances (
    id bigint PRIMARY KEY REFERENCES owner_names(owner_id),
    name text NOT NULL, short_name text NOT NULL, description text NOT NULL, url text NOT NULL,
    executor_corporation_id bigint NOT NULL REFERENCES corporations(id), creator_corporation_id bigint NOT NULL REFERENCES corporations(id),
    creator_character_id bigint NOT NULL, created_ms bigint NOT NULL, founding_key text NOT NULL UNIQUE, deleted boolean NOT NULL DEFAULT false
);
CREATE TABLE alliance_members (
    alliance_id bigint NOT NULL REFERENCES alliances(id), corporation_id bigint NOT NULL UNIQUE REFERENCES corporations(id),
    started_ms bigint NOT NULL, chosen_executor_id bigint NOT NULL, PRIMARY KEY(alliance_id, corporation_id)
);
CREATE TABLE alliance_employment (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, corporation_id bigint NOT NULL,
    alliance_id bigint NOT NULL, started_ms bigint NOT NULL, ended_ms bigint
);
CREATE UNIQUE INDEX alliance_employment_active ON alliance_employment(corporation_id) WHERE ended_ms IS NULL;
CREATE TABLE alliance_applications (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, alliance_id bigint NOT NULL REFERENCES alliances(id),
    corporation_id bigint NOT NULL REFERENCES corporations(id), text text NOT NULL, status integer NOT NULL,
    created_ms bigint NOT NULL, updated_ms bigint NOT NULL, archived boolean NOT NULL DEFAULT false
);
CREATE UNIQUE INDEX alliance_applications_active ON alliance_applications(alliance_id,corporation_id) WHERE NOT archived;
CREATE TABLE role_history (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, corporation_id bigint NOT NULL, character_id bigint NOT NULL,
    actor_id bigint NOT NULL, time_ms bigint NOT NULL, old_roles jsonb NOT NULL, new_roles jsonb NOT NULL
);
CREATE TABLE medals (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, corporation_id bigint NOT NULL REFERENCES corporations(id),
    creator_id bigint NOT NULL, title text NOT NULL, description text NOT NULL, data jsonb NOT NULL, created_ms bigint NOT NULL
);
CREATE TABLE medal_awards (
    medal_id bigint NOT NULL REFERENCES medals(id), character_id bigint NOT NULL, issuer_id bigint NOT NULL,
    reason text NOT NULL, created_ms bigint NOT NULL, status integer NOT NULL DEFAULT 2, PRIMARY KEY(medal_id,character_id)
);
