//! `behaviors/conditions/effects.py`.

use crate::utility::dogmatic::{is_my_effect_active, my_type_id};
use crate::value::Value;

enter_task!(IsEffectActive, |t| {
    let Some(effect_id) = t.required_int("effectId") else { return };
    let active = is_my_effect_active(t, effect_id);
    t.set_status_to_success_if_true_else_to_failed(active);
});

enter_task!(IsEffectByAddressActive, |t| {
    let Some(effect_id) = t.value_at("effectIDAddress") else { return };
    let active = effect_id.as_int().is_some_and(|id| is_my_effect_active(t, id));
    t.set_status_to_success_if_true_else_to_failed(active);
});

enter_task!(IsAnyEffectActive, |t| {
    let Some(effect_ids) = t.required("effectIds") else { return };
    for effect_id in effect_ids.items().unwrap_or(&[]) {
        if let Some(id) = effect_id.as_int()
            && is_my_effect_active(t, id)
        {
            t.set_status_to_success();
            return;
        }
    }
    t.set_status_to_failed();
});

enter_task!(HasAnyEffectCondition, |t| {
    let Some(effect_ids) = t.required("effectIds") else { return };
    let Some(type_id) = my_type_id(t) else { return };
    let has_any = effect_ids
        .items()
        .unwrap_or(&[])
        .iter()
        .filter_map(Value::as_int)
        .any(|id| t.world().type_has_effect(type_id, id));
    t.set_status_to_success_if_true_else_to_failed(has_any);
});

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::SlimItem;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    #[test]
    fn an_effect_is_active_by_id_by_address_and_among_several() {
        let mut rig = Rig::new();
        rig.world.active_effects.insert((1, 55));
        assert_eq!(rig.run(Box::new(IsEffectActive), Attrs::new().with("effectId", 55i64)), Status::Success);
        assert_eq!(rig.run(Box::new(IsEffectActive), Attrs::new().with("effectId", 56i64)), Status::Failure);
        let mut rig = Rig::new();
        rig.world.active_effects.insert((1, 55));
        rig.post(ScopeType::Item, "E", Value::Int(55));
        let by_address = Attrs::new().with("effectIDAddress", Address::new(ScopeType::Item, "E").to_value());
        assert_eq!(rig.run(Box::new(IsEffectByAddressActive), by_address), Status::Success);
        let mut rig = Rig::new();
        rig.world.active_effects.insert((1, 55));
        let effects = |ids: &[i64]| Value::List(ids.iter().map(|i| Value::Int(*i)).collect());
        assert_eq!(
            rig.run(Box::new(IsAnyEffectActive), Attrs::new().with("effectIds", effects(&[1, 55]))),
            Status::Success
        );
        assert_eq!(
            rig.run(Box::new(IsAnyEffectActive), Attrs::new().with("effectIds", effects(&[1, 2]))),
            Status::Failure
        );
    }

    #[test]
    fn my_type_has_any_of_the_effects() {
        let mut rig = Rig::new();
        rig.ctx.my_slim_item = Some(SlimItem { type_id: 500, ..Default::default() });
        rig.world.type_effects.insert((500, 9));
        let effects = |ids: &[i64]| Value::List(ids.iter().map(|i| Value::Int(*i)).collect());
        assert_eq!(
            rig.run(Box::new(HasAnyEffectCondition), Attrs::new().with("effectIds", effects(&[8, 9]))),
            Status::Success
        );
        assert_eq!(
            rig.run(Box::new(HasAnyEffectCondition), Attrs::new().with("effectIds", effects(&[8]))),
            Status::Failure
        );
    }
}
