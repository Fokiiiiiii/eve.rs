CREATE TABLE relations (
    id         bigserial PRIMARY KEY,
    kind       text             NOT NULL,
    subject    bigint           NOT NULL,
    -- 0 when the relation is about the subject alone (security status)
    object     bigint           NOT NULL,
    value      double precision NOT NULL DEFAULT 0,
    attrs      jsonb            NOT NULL DEFAULT '{}',
    since_ms   bigint           NOT NULL,
    -- when it ends by itself (a war that was retracted); a scheduler job ends it then
    until_ms   bigint,
    updated_ms bigint           NOT NULL,
    UNIQUE (kind, subject, object)
);
CREATE INDEX relations_by_object ON relations (kind, object);
CREATE INDEX relations_expiring ON relations (until_ms) WHERE until_ms IS NOT NULL;

CREATE TABLE relation_history (
    id          bigserial PRIMARY KEY,
    relation_id bigint,
    kind        text             NOT NULL,
    subject     bigint           NOT NULL,
    object      bigint           NOT NULL,
    old_value   double precision,
    new_value   double precision,
    reason      text,
    at_ms       bigint           NOT NULL,
    attrs       jsonb
);
CREATE INDEX relation_history_subject ON relation_history (kind, subject, id);
