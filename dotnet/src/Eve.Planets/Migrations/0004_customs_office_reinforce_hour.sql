ALTER TABLE customs_offices
    ADD COLUMN reinforce_hour integer NOT NULL DEFAULT 18 CHECK (reinforce_hour BETWEEN 0 AND 23);
