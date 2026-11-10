CREATE TABLE skin_license_activation_intents (
    intent_id uuid PRIMARY KEY,
    character_id bigint NOT NULL,
    skin_id bigint NOT NULL,
    override_path text NOT NULL,
    override_json jsonb NOT NULL,
    created_at_ms bigint NOT NULL
);

CREATE INDEX skin_license_activation_intents_created
    ON skin_license_activation_intents (created_at_ms, intent_id);
