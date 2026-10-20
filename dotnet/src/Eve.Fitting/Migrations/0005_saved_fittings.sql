CREATE TABLE saved_fitting (
    fitting_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    owner_scope smallint NOT NULL CHECK (owner_scope BETWEEN 1 AND 3),
    owner_id bigint NOT NULL CHECK (owner_id > 0),
    ship_type_id integer NOT NULL CHECK (ship_type_id > 0),
    name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 50),
    description text NOT NULL DEFAULT '' CHECK (char_length(description) <= 500),
    fit_data jsonb NOT NULL CHECK (jsonb_typeof(fit_data) = 'array' AND jsonb_array_length(fit_data) > 0),
    saved_at bigint NOT NULL
);
CREATE INDEX saved_fitting_owner ON saved_fitting (owner_scope, owner_id);
