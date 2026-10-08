ALTER TABLE characters
    ADD COLUMN tutorial_entry_mode text NOT NULL DEFAULT 'disabled'
        CHECK (tutorial_entry_mode IN ('disabled', 'tutorial', 'cinematic_intro_overlay')),
    ADD COLUMN tutorial_first_login_handoff text NOT NULL DEFAULT 'none'
        CHECK (tutorial_first_login_handoff IN ('none', 'cinematic_exit_to_hangar')),
    ADD COLUMN air_npe_state smallint NOT NULL DEFAULT 2
        CHECK (air_npe_state IN (1, 2, 3)),
    ADD COLUMN air_npe_reveal_on_first_login boolean NOT NULL DEFAULT false;
