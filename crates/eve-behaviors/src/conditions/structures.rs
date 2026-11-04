//! `behaviors/conditions/structures.py`.

use crate::utility::ballparks::get_ball;

enter_task!(IsStructureVulnerable, |t| {
    let Some(structure) = t.value_at("structureIdAddress") else { return };
    let Some(structure_id) = t.expect_id(&structure) else { return };
    if get_ball(t, structure_id).is_none() {
        return;
    }
    let vulnerable = t.world().is_structure_vulnerable(structure_id);
    let Some(vulnerable) = t.ok(vulnerable) else { return };
    t.set_status_to_success_if_true_else_to_failed(vulnerable);
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};
    use crate::value::Value;

    #[test]
    fn a_structure_is_vulnerable_when_its_ball_says_so() {
        let attrs = Attrs::new().with("structureIdAddress", Address::new(ScopeType::Item, "S").to_value());
        let mut rig = Rig::new();
        rig.world.add_ball(40, [0.0; 3], 1);
        rig.world.vulnerable_structures.insert(40);
        rig.post(ScopeType::Item, "S", Value::Int(40));
        assert_eq!(rig.run(Box::new(IsStructureVulnerable), attrs.clone()), Status::Success);
        let mut rig = Rig::new();
        rig.world.add_ball(40, [0.0; 3], 1);
        rig.post(ScopeType::Item, "S", Value::Int(40));
        assert_eq!(rig.run(Box::new(IsStructureVulnerable), attrs.clone()), Status::Failure);
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "S", Value::Int(41));
        rig.run(Box::new(IsStructureVulnerable), attrs);
        assert!(rig.last_error.is_some(), "a structure that is not there raises");
    }
}
