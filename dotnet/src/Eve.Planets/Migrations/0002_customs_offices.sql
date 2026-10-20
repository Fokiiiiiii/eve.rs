-- Customs office ownership and tax policy survive host restarts.
CREATE TABLE customs_offices (
    office_id bigint PRIMARY KEY CHECK (office_id > 0),
    owner_corporation_id bigint NOT NULL CHECK (owner_corporation_id > 0),
    allow_alliance boolean NOT NULL DEFAULT true,
    allow_standings boolean NOT NULL DEFAULT true,
    standing_level integer NOT NULL DEFAULT 0 CHECK (standing_level BETWEEN -10 AND 10),
    tax_rates jsonb NOT NULL DEFAULT '{"corporation":0.05,"alliance":0.05,"standingHigh":0.05,"standingGood":0.05,"standingNeutral":0.05,"standingBad":0.05,"standingHorrible":0.05}'::jsonb,
    updated_ms bigint NOT NULL DEFAULT 0
);
