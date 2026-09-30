//! `behaviors/utility/item_modules.py`.

use crate::tree::TaskCtx;

/// `get_fitted_modules`: `(module id, type id, flag id)` of the modules that are online.
pub fn get_fitted_modules(t: &mut TaskCtx<'_, '_>, item_id: i64) -> Vec<(i64, i64, i64)> {
    t.world().online_fitted_modules(item_id)
}

/// `is_type_id_in_type_list`.
pub fn is_type_id_in_type_list(t: &mut TaskCtx<'_, '_>, type_list_id: i64, type_id: i64) -> bool {
    t.world().type_ids_in_list(type_list_id).contains(&type_id)
}

/// `has_fitted_from_type_list`.
pub fn has_fitted_from_type_list(t: &mut TaskCtx<'_, '_>, item_id: i64, type_list_id: i64) -> bool {
    for (_module_id, type_id, _flag_id) in get_fitted_modules(t, item_id) {
        if is_type_id_in_type_list(t, type_list_id, type_id) {
            return true;
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tree::tests::with_task;

    #[test]
    fn an_item_has_fitted_from_a_type_list_when_one_of_its_online_modules_is_of_a_listed_type() {
        let (_, answer) = with_task(
            |rig| {
                rig.world.fitted_modules.insert(1, vec![(10, 100, 27), (11, 200, 28)]);
                rig.world.type_ids_by_list.insert(5, vec![200, 300]);
                rig.world.type_ids_by_list.insert(6, vec![999]);
            },
            |t| {
                (
                    has_fitted_from_type_list(t, 1, 5),
                    has_fitted_from_type_list(t, 1, 6),
                    has_fitted_from_type_list(t, 2, 5),
                )
            },
        );
        assert_eq!(answer, Some((true, false, false)));
    }
}
