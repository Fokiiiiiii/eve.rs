-- Before the standing direction fix, player standings were stored as character → owner.
-- eve.rs character ids are allocated from 140,000,001 (Characters/Migrations/0001_characters.sql).
-- Keep a current-direction row when both forms exist, then transpose the remaining legacy rows.
DELETE FROM relations AS legacy
USING relations AS current
WHERE legacy.kind = 'standing'
  AND legacy.subject >= 140000000
  AND current.kind = 'standing'
  AND current.subject < 140000000
  AND current.subject = legacy.object
  AND current.object = legacy.subject;

UPDATE relations
SET subject = -subject, object = -object
WHERE kind = 'standing' AND subject >= 140000000;

UPDATE relations
SET subject = -object, object = -subject
WHERE kind = 'standing' AND subject < 0 AND object < 0;

UPDATE relation_history
SET subject = object, object = subject
WHERE kind = 'standing' AND subject >= 140000000;
