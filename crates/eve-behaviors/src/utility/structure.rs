//! `behaviors/utility/structure.py`.

use crate::consts::CATEGORY_STRUCTURE;
use crate::env::SlimItem;
use crate::tree::TaskCtx;

/// `can_dock_at_structure`: the structure's docking setting for my group's owner, its faction and no one else.
pub fn can_dock_at_structure(t: &mut TaskCtx<'_, '_>, structure_id: i64) -> Option<bool> {
    let corporation_id = crate::utility::groups::get_my_entity_group_owner_id(t)?;
    let alliance_id = t.world().corporation_faction_id(corporation_id);
    Some(t.world().structure_housing_can_dock(corporation_id, structure_id, corporation_id, alliance_id))
}

/// `is_structure`.
pub fn is_structure(slim_item: &SlimItem) -> bool {
    slim_item.category_id == CATEGORY_STRUCTURE
}
