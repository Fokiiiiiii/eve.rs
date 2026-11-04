//! `behaviors/actions/tale.py`.

enter_task!(
    /// Gives an object that was saved the tale it belongs to (fails when the tale manager knows of none).
    UpdateTaleContextForPersistedObject,
    |t| {
        t.set_status_to_success();
        if t.ctx().my_tale_id.is_some() {
            return;
        }
        let Some(me) = t.my_item_id() else { return };
        let tale_id = t.world().tale_id_for_persisted_object(me);
        if tale_id.is_none_or(|tale| tale == 0) {
            t.set_status_to_failed();
        }
        t.ctx().my_tale_id = tale_id;
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    #[test]
    fn the_tale_comes_from_the_tale_manager_and_none_fails_the_task() {
        let mut rig = Rig::new();
        rig.world.persisted_tales.insert(1, 33);
        assert_eq!(rig.run(Box::new(UpdateTaleContextForPersistedObject), Attrs::new()), Status::Success);
        assert_eq!(rig.ctx.my_tale_id, Some(33));
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(UpdateTaleContextForPersistedObject), Attrs::new()), Status::Failure);
        assert_eq!(rig.ctx.my_tale_id, None);
        let mut rig = Rig::new();
        rig.ctx.my_tale_id = Some(5);
        rig.world.persisted_tales.insert(1, 33);
        rig.run(Box::new(UpdateTaleContextForPersistedObject), Attrs::new());
        assert_eq!(rig.ctx.my_tale_id, Some(5), "a task that has a tale keeps it");
    }
}
