-- The moon extractions of the Standup Moon Drill (ST-6a; EVE.js moonExtractions.extractions). One row per extraction: state 'active' (the chunk grows),
-- 'fracturing' (the drill fired, the fracture completes after 30 s) or 'completed' (completed_reason: fractured, cancelled, decayed or offlined). Times are
-- milliseconds since 1970, 0 = none. ore_volume holds the m3 of each ore type id. The client reads active rows only, so reads skip the completed history.
CREATE TABLE structure_moon_extractions (
    extraction_id        bigint           PRIMARY KEY,
    structure_id         bigint           NOT NULL,
    moon_id              bigint           NOT NULL DEFAULT 0,
    solar_system_id      bigint           NOT NULL DEFAULT 0,
    owner_corporation_id bigint           NOT NULL DEFAULT 0,
    start_ms             bigint           NOT NULL,
    chunk_available_ms   bigint           NOT NULL,
    natural_decay_ms     bigint           NOT NULL,
    duration_seconds     bigint           NOT NULL,
    yield_multiplier     double precision NOT NULL DEFAULT 1,
    calendar_event_id    bigint           NOT NULL DEFAULT 0,
    state                text             NOT NULL,
    ready_notified_ms    bigint           NOT NULL DEFAULT 0,
    fracture_started_ms  bigint           NOT NULL DEFAULT 0,
    fracture_complete_ms bigint           NOT NULL DEFAULT 0,
    ore_volume           jsonb            NOT NULL DEFAULT '{}',
    created_ms           bigint           NOT NULL DEFAULT 0,
    completed_ms         bigint           NOT NULL DEFAULT 0,
    completed_reason     text             NOT NULL DEFAULT ''
);
CREATE INDEX structure_moon_extractions_structure_idx ON structure_moon_extractions (structure_id) WHERE state <> 'completed';
CREATE INDEX structure_moon_extractions_owner_idx ON structure_moon_extractions (owner_corporation_id) WHERE state <> 'completed';

-- The resource profile of a structure (EVE.js resourcesByStructureID): the moon it mines, its yield and the share of each ore type id.
CREATE TABLE structure_moon_resources (
    structure_id         bigint           PRIMARY KEY,
    moon_id              bigint           NOT NULL DEFAULT 0,
    solar_system_id      bigint           NOT NULL DEFAULT 0,
    owner_corporation_id bigint           NOT NULL DEFAULT 0,
    yield_multiplier     double precision NOT NULL DEFAULT 1,
    resources            jsonb            NOT NULL DEFAULT '{}'
);
