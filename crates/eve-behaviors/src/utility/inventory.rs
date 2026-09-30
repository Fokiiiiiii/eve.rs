//! `behaviors/utility/inventory.py`.

use crate::consts::FLAG_CARGO;
use crate::env::{CargoCapacity, InventoryItem};
use crate::tree::TaskCtx;

/// `get_inventory_item`: an item that is not there is `None`.
pub fn get_inventory_item(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<InventoryItem> {
    t.world().inventory_item(item_id)
}

/// `get_type_id_by_item_id`.
pub fn get_type_id_by_item_id(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<i64> {
    get_inventory_item(t, item_id).map(|item| item.type_id)
}

/// `get_cargo_capacity`.
pub fn get_cargo_capacity(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<CargoCapacity> {
    let capacity = t.world().cargo_capacity(item_id);
    t.ok(capacity)
}

/// `is_my_cargo_full`.
pub fn is_my_cargo_full(t: &mut TaskCtx<'_, '_>) -> bool {
    let Some(me) = t.my_item_id() else { return false };
    match get_cargo_capacity(t, me) {
        Some(cargo) => cargo.used + 1e-07 >= cargo.capacity,
        None => false,
    }
}

/// `is_my_cargo_empty`.
pub fn is_my_cargo_empty(t: &mut TaskCtx<'_, '_>) -> bool {
    let Some(me) = t.my_item_id() else { return false };
    match get_cargo_capacity(t, me) {
        Some(cargo) => cargo.used == 0.0,
        None => false,
    }
}

/// `get_cargo_used_fraction`: a cargo bay with no capacity raises what a division by zero does.
pub fn get_cargo_used_fraction(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<f64> {
    let cargo = get_cargo_capacity(t, item_id)?;
    if cargo.capacity == 0.0 {
        t.raise(crate::tree::BehaviorError::Task("ZeroDivisionError: float division by zero".to_string()));
        return None;
    }
    Some(cargo.used / cargo.capacity)
}

/// `get_cargo_used`.
pub fn get_cargo_used(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<f64> {
    get_cargo_capacity(t, item_id).map(|cargo| cargo.used)
}

/// `get_cargo_capacity_max`.
pub fn get_cargo_capacity_max(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Option<f64> {
    get_cargo_capacity(t, item_id).map(|cargo| cargo.capacity)
}

/// `remove_all_items`: destroys what is inside an item.
pub fn remove_all_items(t: &mut TaskCtx<'_, '_>, item_id: i64) {
    for inner in t.world().select_items(item_id) {
        t.world().destroy_item(inner);
    }
}

/// `add_type_to_cargo`.
pub fn add_type_to_cargo(t: &mut TaskCtx<'_, '_>, item_id: i64, type_id: i64, amount: i64) {
    t.world().manufacture_item_raw(type_id, amount, item_id, FLAG_CARGO);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::tests::with_task;

    #[test]
    fn my_cargo_is_full_within_a_hair_and_empty_at_zero_and_a_cargo_of_nothing_raises_for_a_fraction() {
        let (_, answer) = with_task(
            |rig| {
                rig.world.cargo.insert(1, CargoCapacity { used: 99.999_999_95, capacity: 100.0 });
                rig.world.cargo.insert(2, CargoCapacity { used: 0.0, capacity: 0.0 });
            },
            |t| (is_my_cargo_full(t), is_my_cargo_empty(t), get_cargo_used_fraction(t, 1)),
        );
        let (full, empty, fraction) = answer.unwrap();
        assert!(full && !empty);
        assert!((fraction.unwrap() - 0.999_999_999_5).abs() < 1e-12);
        let (rig, _) = with_task(
            |rig| {
                rig.world.cargo.insert(2, CargoCapacity::default());
            },
            |t| get_cargo_used_fraction(t, 2),
        );
        assert!(rig.last_error.is_some());
    }

    #[test]
    fn everything_inside_an_item_is_destroyed_and_cargo_is_added_to_the_cargo_flag() {
        let (mut rig, _) = with_task(
            |rig| {
                rig.world.cargo_contents.insert(5, vec![50, 51]);
            },
            |t| {
                remove_all_items(t, 5);
                add_type_to_cargo(t, 5, 34, 100);
            },
        );
        assert_eq!(
            rig.world.take_calls(),
            ["destroy_item(50)", "destroy_item(51)", "manufacture_item_raw(34, 100, 5, 5)"]
        );
    }
}
