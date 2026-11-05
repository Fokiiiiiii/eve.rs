-- The recustomization state of a character (EveJS Paperdoll.State): 1 resculpting (one resculpt that keeps the gender) and 3 full recustomizing.
-- NULL is no state. A save of the appearance ends the state.
ALTER TABLE characters
    ADD COLUMN paperdoll_state smallint CHECK (paperdoll_state IN (1, 3));
