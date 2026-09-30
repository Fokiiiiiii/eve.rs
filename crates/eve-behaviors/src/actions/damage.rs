//! `behaviors/actions/damage.py`: setting how much health an item has left, and damage that reaches ships outside of an area.

use crate::consts::{
    ATTRIBUTE_EM_DAMAGE, ATTRIBUTE_EXPLOSIVE_DAMAGE, ATTRIBUTE_KINETIC_DAMAGE, ATTRIBUTE_THERMAL_DAMAGE,
};
use crate::tree::{BehaviorError, TaskCtx};
use crate::utility::ballparks::get_distance_between;
use crate::value::Value;

enter_task!(
    /// Sets the armor of my entity to a ratio of what it can have.
    SetArmorRatio,
    |t| {
        let Some(me) = t.my_item_id() else { return };
        let Some(ratio) = t.required("armorRatio") else { return };
        t.world().set_armor_ratio(me, &ratio);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Sets the shield of my entity to a ratio of what it can have.
    SetShieldRatio,
    |t| {
        let Some(me) = t.my_item_id() else { return };
        let Some(ratio) = t.required("shieldRatio") else { return };
        t.world().set_shield_ratio(me, &ratio);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Sets the structure of my entity to a ratio of what it can have.
    SetStructureRatio,
    |t| {
        let Some(me) = t.my_item_id() else { return };
        let Some(ratio) = t.required("structureRatio") else { return };
        t.world().set_structure_ratio(me, &ratio);
        t.set_status_to_success();
    }
);

/// The item on the blackboard of `itemIdAddress`, when there is one (an id that is 0 is none).
fn item_for_ratio(t: &mut TaskCtx<'_, '_>) -> Option<Option<i64>> {
    let address = t.required_address("itemIdAddress")?;
    let item_id = t.get_last_blackboard_value(&address);
    Some(if item_id.truthy() { item_id.as_int() } else { None })
}

enter_task!(
    /// Sets the armor of the item on the blackboard.
    SetArmorRatioForItem,
    |t| {
        let Some(item_id) = item_for_ratio(t) else { return };
        if let Some(item_id) = item_id {
            let Some(ratio) = t.required("armorRatio") else { return };
            t.world().set_armor_ratio(item_id, &ratio);
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Sets the shield of the item on the blackboard.
    SetShieldRatioForItem,
    |t| {
        let Some(item_id) = item_for_ratio(t) else { return };
        if let Some(item_id) = item_id {
            let Some(ratio) = t.required("shieldRatio") else { return };
            t.world().set_shield_ratio(item_id, &ratio);
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Sets the structure of the item on the blackboard.
    SetStructureRatioForItem,
    |t| {
        let Some(item_id) = item_for_ratio(t) else { return };
        if let Some(item_id) = item_id {
            let Some(ratio) = t.required("structureRatio") else { return };
            t.world().set_structure_ratio(item_id, &ratio);
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Hurts the ships in my bubble that are further than the minimum distance from me, the more the further they are.
    DealDamageOutsideOfArea,
    |t| {
        t.set_status_to_success();
        let Some(me) = t.my_item_id() else { return };
        let ships_in_bubble = t.world().ships_in_same_bubble(me);
        let Some(min_damage_distance) = t.required_float("minDamageDistance") else {
            return;
        };
        // `_get_ships_within_damage_distance`
        let mut ships_within_damage_distance = Vec::new();
        for ship_id in ships_in_bubble {
            let Some(distance) = get_distance_between(t, me, ship_id) else { return };
            if min_damage_distance < distance {
                ships_within_damage_distance.push((ship_id, distance));
            }
        }
        for (ship_id, distance) in ships_within_damage_distance {
            let (Some(max_damage_distance), Some(area_damage_factor)) =
                (t.required_float("maxDamageDistance"), t.required_float("areaDamageFactor"))
            else {
                return;
            };
            // `_get_damage_based_on_distance`
            let distance_from_min = distance - min_damage_distance;
            let area_distance = max_damage_distance - min_damage_distance;
            if area_distance == 0.0 {
                t.raise(BehaviorError::Task("ZeroDivisionError: float division by zero".to_string()));
                return;
            }
            let damage_multiplier = (distance_from_min / area_distance).exp().powf(area_damage_factor);
            // `_deal_damage_to_ship`
            let warping = t.world().is_warping(ship_id);
            let Some(warping) = t.ok(warping) else { return };
            if warping == 0 {
                let (Some(em), Some(explosive), Some(kinetic), Some(thermal)) = (
                    t.required_float("emDamage"),
                    t.required_float("explosiveDamage"),
                    t.required_float("kineticDamage"),
                    t.required_float("thermalDamage"),
                ) else {
                    return;
                };
                // the client maps each of these to the kind of damage that follows it in the list
                let damage_by_type = [
                    (ATTRIBUTE_EM_DAMAGE, em),
                    (ATTRIBUTE_KINETIC_DAMAGE, explosive),
                    (ATTRIBUTE_THERMAL_DAMAGE, kinetic),
                    (ATTRIBUTE_EXPLOSIVE_DAMAGE, thermal),
                ];
                t.world().apply_damage_with_multiplier(me, ship_id, damage_multiplier, &damage_by_type);
            }
            // `_show_damage_effect_on_ship`
            t.world().start_ship_effect(ship_id, "effects.DrifterControlled", 5000, 0);
        }
    }
);

enter_task!(
    /// Tells the clients in the system that a cholesterol field is on, and how far it reaches.
    DisplayCholesterolField,
    |t| {
        t.set_status_to_success();
        let Some(me) = t.my_item_id() else { return };
        let distance = if t.attribute("distanceAddress").truthy() {
            let Some(address) = t.required_address("distanceAddress") else { return };
            t.get_last_blackboard_value(&address)
        } else {
            let Some(default_distance) = t.required("defaultDistance") else { return };
            default_distance
        };
        let system = t.ctx().solar_system_id;
        t.world().singlecast_by_solar_system_id(system, "OnCholesterolFieldActivated", &[Value::Int(me), distance]);
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    #[test]
    fn the_health_of_my_entity_and_of_an_item_on_the_blackboard_is_set_to_a_ratio() {
        let mut rig = Rig::new();
        assert_eq!(rig.run(Box::new(SetArmorRatio), Attrs::new().with("armorRatio", 0.5)), Status::Success);
        assert_eq!(
            rig.run(Box::new(SetShieldRatio), Attrs::new().with("shieldRatio", 0.25)),
            Status::Success
        );
        assert_eq!(
            rig.run(Box::new(SetStructureRatio), Attrs::new().with("structureRatio", 1i64)),
            Status::Success
        );
        assert_eq!(
            rig.world.take_calls(),
            [
                "set_armor_ratio(1, Float(0.5))",
                "set_shield_ratio(1, Float(0.25))",
                "set_structure_ratio(1, Int(1))"
            ]
        );
    }

    #[test]
    fn the_health_of_the_item_on_the_blackboard_is_set_only_when_there_is_one() {
        let mut rig = Rig::new();
        let attrs = |ratio: &str| Attrs::new().with("itemIdAddress", item("Item")).with(ratio, 0.5);
        assert_eq!(rig.run(Box::new(SetArmorRatioForItem), attrs("armorRatio")), Status::Success);
        assert!(rig.world.take_calls().is_empty(), "no item");
        rig.post(ScopeType::Item, "Item", Value::Int(0));
        rig.run(Box::new(SetArmorRatioForItem), attrs("armorRatio"));
        assert!(rig.world.take_calls().is_empty(), "item 0 is no item");
        rig.post(ScopeType::Item, "Item", Value::Int(77));
        rig.run(Box::new(SetArmorRatioForItem), attrs("armorRatio"));
        rig.run(Box::new(SetShieldRatioForItem), attrs("shieldRatio"));
        rig.run(Box::new(SetStructureRatioForItem), attrs("structureRatio"));
        assert_eq!(
            rig.world.take_calls(),
            [
                "set_armor_ratio(77, Float(0.5))",
                "set_shield_ratio(77, Float(0.5))",
                "set_structure_ratio(77, Float(0.5))"
            ]
        );
    }

    fn area_attrs() -> Attrs {
        Attrs::new()
            .with("minDamageDistance", 10_000.0)
            .with("maxDamageDistance", 30_000.0)
            .with("areaDamageFactor", 2.0)
            .with("emDamage", 1.0)
            .with("explosiveDamage", 2.0)
            .with("kineticDamage", 3.0)
            .with("thermalDamage", 4.0)
    }

    #[test]
    fn ships_beyond_the_minimum_distance_are_hurt_by_how_far_they_are_and_those_in_warp_are_not() {
        let mut rig = Rig::new();
        rig.world.same_bubble_ships.insert(1, vec![20, 21, 22, 23, 24]);
        for (ship, distance) in
            [(20, 5_000.0), (21, 20_000.0), (22, 30_000.0), (23, 30_000.0), (24, 10_000.0)]
        {
            rig.world.set_dist(1, ship, distance);
        }
        rig.world.warping.extend([(20, 0), (21, 0), (22, 0), (23, 3), (24, 0)]);
        assert_eq!(rig.run(Box::new(DealDamageOutsideOfArea), area_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        let calls = rig.world.take_calls();
        // 21: exp(0.5)^2 = e; 22: exp(1)^2 = e^2; the client's damage kinds are shifted by one
        assert_eq!(
            calls,
            [
                "apply_damage_with_multiplier(1, 21, 2.7183, [(114, 1.0), (117, 2.0), (118, 3.0), (116, 4.0)])",
                "start_ship_effect(21, effects.DrifterControlled, 5000, 0)",
                "apply_damage_with_multiplier(1, 22, 7.3891, [(114, 1.0), (117, 2.0), (118, 3.0), (116, 4.0)])",
                "start_ship_effect(22, effects.DrifterControlled, 5000, 0)",
                "start_ship_effect(23, effects.DrifterControlled, 5000, 0)",
            ]
        );
    }

    #[test]
    fn the_cholesterol_field_tells_the_system_how_far_it_reaches() {
        let mut rig = Rig::new();
        rig.ctx.solar_system_id = Some(30000142);
        let attrs = Attrs::new().with("defaultDistance", 20_000i64);
        assert_eq!(rig.run(Box::new(DisplayCholesterolField), attrs.clone()), Status::Success);
        assert_eq!(
            rig.world.take_calls(),
            ["singlecast(Some(30000142), OnCholesterolFieldActivated, [Int(1), Int(20000)])"]
        );
        rig.post(ScopeType::Item, "Distance", Value::Float(5000.0));
        rig.run(Box::new(DisplayCholesterolField), attrs.with("distanceAddress", item("Distance")));
        assert_eq!(
            rig.world.take_calls(),
            ["singlecast(Some(30000142), OnCholesterolFieldActivated, [Int(1), Float(5000.0)])"]
        );
    }
}
