CREATE TABLE accounts (
    id            integer GENERATED ALWAYS AS IDENTITY (START WITH 1) PRIMARY KEY,
    user_name     text    NOT NULL UNIQUE,
    password_hash bytea   NOT NULL,
    -- the account's role mask (the client shows it as a 64-bit integer); 0 is an ordinary player
    role          bigint  NOT NULL DEFAULT 0,
    banned        boolean NOT NULL DEFAULT false,
    country_code  text    NOT NULL DEFAULT 'GB' CHECK (country_code ~ '^[A-Z]{2}$'),
    created_at    timestamptz NOT NULL DEFAULT now(),
    last_login_at timestamptz
);
