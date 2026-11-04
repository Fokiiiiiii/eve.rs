//! `behaviors/actions/structures.py`.

use crate::consts::{CATEGORY_STATION, CATEGORY_STRUCTURE, is_npc_corporation};
use crate::env::SlimItem;
use crate::groups::mixin::{get_group_faction_id, get_group_owner_id};
use crate::tree::TaskCtx;
use crate::utility::standings::{STANDINGS_NEUTRAL, classify_standings_between};
use crate::utility::structure::{can_dock_at_structure, is_structure};
use crate::value::Value;

/// `has_structure_preference`.
fn has_structure_preference(t: &mut TaskCtx<'_, '_>) -> bool {
    !t.attribute("preferredStructureGroupId").is_none()
}

/// `is_preferred_structure`.
fn is_preferred_structure(t: &mut TaskCtx<'_, '_>, item: &SlimItem) -> bool {
    Value::Int(item.group_id).py_eq(&t.attribute("preferredStructureGroupId"))
}

/// One of the filter policies of `get_prioritized_filter_policies`, in the order they are tried.
#[derive(Debug, Clone, Copy)]
enum Policy {
    MyCorpPreferredStructure,
    MyCorpStructures,
    MyFactionPreferredStructure,
    MyFactionStructures,
    NpcStructuresWithPositiveStandings,
    PlayerStructuresWithPositiveStandings,
}

const POLICIES: [Policy; 6] = [
    Policy::MyCorpPreferredStructure,
    Policy::MyCorpStructures,
    Policy::MyFactionPreferredStructure,
    Policy::MyFactionStructures,
    Policy::NpcStructuresWithPositiveStandings,
    Policy::PlayerStructuresWithPositiveStandings,
];

/// The structures a policy accepts.
fn apply_policy(
    t: &mut TaskCtx<'_, '_>,
    policy: Policy,
    slim_items: &[SlimItem],
    owner_id: Option<i64>,
    faction_id: Option<i64>,
) -> Vec<i64> {
    let mut ids = Vec::new();
    match policy {
        Policy::MyCorpPreferredStructure | Policy::MyFactionPreferredStructure
            if !has_structure_preference(t) => {}
        Policy::MyCorpPreferredStructure => {
            for item in slim_items {
                if item.owner_id == owner_id && is_preferred_structure(t, item) {
                    ids.push(item.item_id);
                }
            }
        }
        Policy::MyFactionPreferredStructure => {
            for item in slim_items {
                let faction = item.owner_id.and_then(|o| t.world().corporation_faction_id(o));
                if faction == faction_id && is_preferred_structure(t, item) {
                    ids.push(item.item_id);
                }
            }
        }
        Policy::MyCorpStructures => {
            ids.extend(slim_items.iter().filter(|i| i.owner_id == owner_id).map(|i| i.item_id))
        }
        Policy::MyFactionStructures => {
            for item in slim_items {
                let faction = item.owner_id.and_then(|o| t.world().corporation_faction_id(o));
                if faction == faction_id {
                    ids.push(item.item_id);
                }
            }
        }
        Policy::NpcStructuresWithPositiveStandings => {
            for item in slim_items {
                if item.owner_id.is_some_and(is_npc_corporation)
                    && let Some(owner) = item.owner_id
                    && classify_standings_between(t, owner).is_some_and(|c| c > STANDINGS_NEUTRAL)
                {
                    ids.push(item.item_id);
                }
                if t.faulted() {
                    return ids;
                }
            }
        }
        Policy::PlayerStructuresWithPositiveStandings => {
            for item in slim_items {
                let Some(owner) = item.owner_id else { continue };
                if is_npc_corporation(owner) {
                    continue;
                }
                if classify_standings_between(t, owner).is_some_and(|c| c > STANDINGS_NEUTRAL)
                    && (!is_structure(item) || can_dock_at_structure(t, item.item_id) == Some(true))
                {
                    ids.push(item.item_id);
                }
                if t.faulted() {
                    return ids;
                }
            }
        }
    }
    ids
}

enter_task!(
    /// Selects a structure or a station of the system that my group may dock at, by preference, and puts it on a channel.
    SelectDockableStructure,
    |t| {
        t.set_status_to_failed();
        let slim_items: Vec<SlimItem> = t.world().slim_items().into_iter().filter(|s| s.category_id == CATEGORY_STRUCTURE || s.category_id == CATEGORY_STATION).collect();
        if slim_items.is_empty() {
            return;
        }
        let owner_id = get_group_owner_id(t);
        let faction_id = get_group_faction_id(t);
        if t.faulted() {
            return;
        }
        let mut selected = None;
        for policy in POLICIES {
            let valid = apply_policy(t, policy, &slim_items, owner_id, faction_id);
            if t.faulted() {
                return;
            }
            if valid.is_empty() {
                continue;
            }
            let at = t.world().random_index(valid.len());
            selected = Some(valid[at]);
            break;
        }
        let Some(selected) = selected.filter(|s| *s != 0) else { return };
        let Some(address) = t.required_address("selectedStructureIdAddress") else { return };
        t.send_blackboard_value(&address, Value::Int(selected));
        t.set_status_to_success();
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::ResponseThresholds;
    use crate::env::testing::TestGroup;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.ctx.my_entity_group_id = Some(10);
        rig.world.groups.insert(10, TestGroup { owner_id: Some(1000006), ..Default::default() });
        rig.world.factions.insert(1000006, 500001);
        rig.world.factions.insert(1000007, 500001);
        rig.world.thresholds =
            Some(ResponseThresholds { hostile_response_threshold: -1.0, friendly_response_threshold: 1.0 });
        rig
    }

    fn structure(rig: &mut Rig, id: i64, category: i64, group: i64, owner: i64) {
        rig.world.add_ball(id, [0.0; 3], 1);
        let slim = rig.world.slims.get_mut(&id).unwrap();
        slim.category_id = category;
        slim.group_id = group;
        slim.owner_id = Some(owner);
    }

    fn attrs() -> Attrs {
        Attrs::new().with("selectedStructureIdAddress", Address::new(ScopeType::Item, "Chosen").to_value())
    }

    #[test]
    fn my_corps_preferred_structure_comes_first_then_my_corps_then_my_factions() {
        let mut rig = rig();
        structure(&mut rig, 100, CATEGORY_STATION, 15, 1000007);
        structure(&mut rig, 101, CATEGORY_STATION, 15, 1000006);
        structure(&mut rig, 102, CATEGORY_STRUCTURE, 99, 1000006);
        assert_eq!(
            rig.run(Box::new(SelectDockableStructure), attrs().with("preferredStructureGroupId", 99i64)),
            Status::Success
        );
        assert_eq!(rig.last(ScopeType::Item, "Chosen"), Value::Int(102));
        let mut rig2 = self::rig();
        structure(&mut rig2, 100, CATEGORY_STATION, 15, 1000007);
        structure(&mut rig2, 101, CATEGORY_STATION, 15, 1000006);
        rig2.run(Box::new(SelectDockableStructure), attrs());
        assert_eq!(rig2.last(ScopeType::Item, "Chosen"), Value::Int(101), "my corp's structure");
        let mut rig3 = self::rig();
        structure(&mut rig3, 100, CATEGORY_STATION, 15, 1000007);
        rig3.run(Box::new(SelectDockableStructure), attrs());
        assert_eq!(rig3.last(ScopeType::Item, "Chosen"), Value::Int(100), "my faction's structure");
    }

    #[test]
    fn a_player_structure_needs_a_friendly_standing_and_the_right_to_dock_and_there_may_be_none() {
        let mut rig = rig();
        structure(&mut rig, 100, CATEGORY_STRUCTURE, 15, 98_000_001);
        rig.ctx.my_slim_item = Some(SlimItem { owner_id: Some(1000006), ..Default::default() });
        rig.world.standings.insert((1000006, 98_000_001), 5.0);
        assert_eq!(rig.run(Box::new(SelectDockableStructure), attrs()), Status::Failure, "may not dock");
        rig.world.can_dock.insert((1000006, 100));
        assert_eq!(rig.run(Box::new(SelectDockableStructure), attrs()), Status::Success);
        assert_eq!(
            Rig::new().run(Box::new(SelectDockableStructure), attrs()),
            Status::Failure,
            "no structures"
        );
    }
}
