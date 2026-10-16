-- ST-17 a: a hub destroyed by its command node stage: 'destroy-pending' until the sovereignty records have lost it (the destruction job), then 'destroyed'.
ALTER TABLE hubs DROP CONSTRAINT hubs_handover_check;
ALTER TABLE hubs ADD CONSTRAINT hubs_handover_check CHECK (
    handover IS NULL OR handover IN ('pending', 'claimed', 'superseded', 'owned', 'destroy-pending', 'destroyed'));
