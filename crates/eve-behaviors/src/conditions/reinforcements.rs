//! `behaviors/conditions/reinforcements.py`.

enter_task!(AreAnyReinforcementMembersAlive, |t| {
    t.set_status_to_failed();
    let Some(group_id) = t.ctx().my_entity_group_id else {
        t.raise(crate::env::WorldError::Key("myEntityGroupId".to_string()).into());
        return;
    };
    for entity_id in t.world().active_reinforced_entity_ids(group_id) {
        if t.world().has_ball(entity_id) {
            t.set_status_to_success();
            break;
        }
    }
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    #[test]
    fn a_reinforcement_member_is_alive_while_its_ball_is_in_the_park() {
        let mut rig = Rig::new();
        rig.world.reinforced.insert(10, vec![70, 71]);
        rig.world.add_ball(71, [0.0; 3], 1);
        assert_eq!(rig.run(Box::new(AreAnyReinforcementMembersAlive), Attrs::new()), Status::Success);
        let mut rig = Rig::new();
        rig.world.reinforced.insert(10, vec![70]);
        assert_eq!(rig.run(Box::new(AreAnyReinforcementMembersAlive), Attrs::new()), Status::Failure);
    }
}
