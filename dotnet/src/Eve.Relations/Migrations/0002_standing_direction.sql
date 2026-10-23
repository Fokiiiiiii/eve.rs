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
