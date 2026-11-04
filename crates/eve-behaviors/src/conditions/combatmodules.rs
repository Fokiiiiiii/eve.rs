//! `behaviors/conditions/combatmodules.py`.

enter_task!(IsModuleActive, |t| {
    t.set_status_to_failed();
    let Some(module) = t.value_at("moduleAddress") else { return };
    if !module.truthy() {
        return;
    }
    let Some(module_id) = t.expect_id(&module) else { return };
    if !t.world().is_item_active(module_id) {
        return;
    }
    t.set_status_to_success();
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};
    use crate::value::Value;

    #[test]
    fn a_module_is_active_when_the_dogma_engine_says_so() {
        let attrs = Attrs::new().with("moduleAddress", Address::new(ScopeType::Item, "M").to_value());
        let mut rig = Rig::new();
        rig.world.active_items.insert(30);
        rig.post(ScopeType::Item, "M", Value::Int(30));
        assert_eq!(rig.run(Box::new(IsModuleActive), attrs.clone()), Status::Success);
        let mut rig = Rig::new();
        rig.post(ScopeType::Item, "M", Value::Int(30));
        assert_eq!(rig.run(Box::new(IsModuleActive), attrs.clone()), Status::Failure);
        assert_eq!(Rig::new().run(Box::new(IsModuleActive), attrs), Status::Failure);
    }
}
