//! `behaviors/actions/fleet.py`: warping a group in formation, lining it up, and waiting for its warp to end.

use crate::consts::DESTINY_MODE_WARP;
use crate::env::FormationSettings;
use crate::events::message;
use crate::groups::mixin::get_member_id_list;
use crate::tree::{BehaviorError, Task, TaskCtx, blocking};
use crate::utility::owner::is_eve_player_character;
use crate::value::Value;

/// `add_additional_fleet_members` of `FormationWarpGroup`: the ships of `additionalFleetMembersAddress` that are not members and are in the park, with
/// the character of the ones that a player flies.
fn add_additional_fleet_members_with_characters(
    t: &mut TaskCtx<'_, '_>,
    candidates: &mut Vec<(i64, Option<i64>)>,
) {
    let Some(address) = t.required_address("additionalFleetMembersAddress") else { return };
    let additional_candidates = t.get_last_blackboard_value(&address);
    if !additional_candidates.truthy() {
        return;
    }
    for member in additional_candidates.items().unwrap_or(&[]) {
        let Some(member_id) = member.as_int() else { continue };
        if candidates.iter().any(|(id, _)| *id == member_id) {
            continue;
        }
        let Some(slim) = t.world().slim_item(member_id) else { continue };
        let char_id = if is_eve_player_character(t, slim.owner_id) { slim.owner_id } else { None };
        candidates.push((member_id, char_id));
    }
}

/// Warps the members of my group, and other ships, in a formation.
#[derive(Debug, Default)]
pub struct FormationWarpGroup;

impl FormationWarpGroup {
    /// `get_fleet_warp_candidates`: the members, and the additional ones (each with the character that flies it, if one does).
    fn get_fleet_warp_candidates(t: &mut TaskCtx<'_, '_>) -> Option<Vec<(i64, Option<i64>)>> {
        let mut candidates: Vec<(i64, Option<i64>)> =
            get_member_id_list(t).into_iter().map(|member| (member, None)).collect();
        if t.faulted() {
            return None;
        }
        add_additional_fleet_members_with_characters(t, &mut candidates);
        Some(candidates)
    }

    /// `warp_player`.
    fn warp_player(
        t: &mut TaskCtx<'_, '_>,
        ship_id: i64,
        destination: [f64; 3],
        jitter_radius: f64,
        min_range: f64,
        warp_speed: f64,
    ) {
        if t.world().cancel_if_doing_stuff(ship_id) {
            return;
        }
        t.world().warp_to(ship_id, destination, min_range, warp_speed, jitter_radius);
    }

    /// `warp_npc`: a ball that is gone or cannot warp as an NPC is left; what the warp raises is only logged.
    fn warp_npc(
        t: &mut TaskCtx<'_, '_>,
        ship_id: i64,
        destination: [f64; 3],
        jitter_radius: f64,
        min_range: f64,
        warp_speed: f64,
    ) {
        if !t.world().has_ball(ship_id) {
            return;
        }
        let _ = t.world().npc_warp_to(ship_id, destination, jitter_radius, min_range, warp_speed);
    }
}

impl Task for FormationWarpGroup {
    fn type_name(&self) -> &'static str {
        "FormationWarpGroup"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        // `get_formation_picker`
        let Some(address) = t.required_address("destinationCoordinatesAddress") else { return };
        let destination_coordinates = t.get_last_blackboard_value(&address);
        let Some(jump_candidates) = Self::get_fleet_warp_candidates(t) else { return };
        if jump_candidates.is_empty() {
            t.raise(BehaviorError::Task("IndexError: list index out of range".to_string()));
            return;
        }
        let (Some(formation_type), Some(formation_spacing), Some(formation_size)) =
            (t.required("formationType"), t.required("formationSpacing"), t.required("formationSize"))
        else {
            return;
        };
        let settings = FormationSettings { formation_type, formation_spacing, formation_size };
        let formation = t.world().pick_formation(&jump_candidates, &destination_coordinates, &settings);
        for (ship_id, char_id) in formation.fleet_candidates.iter().copied() {
            let Some((_, destination)) =
                formation.destination_coordinates.iter().find(|(id, _)| *id == ship_id)
            else {
                t.raise(crate::env::WorldError::Key(ship_id.to_string()).into());
                return;
            };
            let destination = *destination;
            if char_id.is_some_and(|char_id| char_id != 0) {
                if t.world().cancel_if_doing_stuff(ship_id) {
                    // the client logs an error and goes on to the next
                    continue;
                }
                Self::warp_player(
                    t,
                    ship_id,
                    destination,
                    formation.jitter_radius,
                    formation.minimum_range,
                    formation.min_warp_speed,
                );
            } else {
                Self::warp_npc(
                    t,
                    ship_id,
                    destination,
                    formation.jitter_radius,
                    formation.minimum_range,
                    formation.min_warp_speed,
                );
            }
        }
        t.set_status_to_success();
    }
}

/// Points the members of my group, and other ships, at a place at full speed.
#[derive(Debug, Default)]
pub struct AlignFleet;

impl Task for AlignFleet {
    fn type_name(&self) -> &'static str {
        "AlignFleet"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        // `get_fleet_candidates`
        let mut candidates = get_member_id_list(t);
        if t.faulted() {
            return;
        }
        let Some(additional_address) = t.required_address("additionalFleetMembersAddress") else { return };
        let additional_candidates = t.get_last_blackboard_value(&additional_address);
        if additional_candidates.truthy() {
            for member in additional_candidates.items().unwrap_or(&[]) {
                if let Some(member_id) = member.as_int()
                    && t.world().slim_item(member_id).is_some()
                {
                    candidates.push(member_id);
                }
            }
        }
        let Some(address) = t.required_address("destinationCoordinatesAddress") else { return };
        let destination_coordinates = t.get_last_blackboard_value(&address);
        for member_id in candidates {
            // whatever goes wrong for one member is only logged
            let Some(destination) = destination_coordinates.as_vec3() else { continue };
            if t.world().goto_point(member_id, destination).is_ok() {
                let _ = t.world().set_speed_fraction(member_id, 1.0);
            }
        }
        t.set_status_to_success();
    }
}

const HANDLER_DESTINY_MODE_CHANGE: u16 = 0;

/// `_destiny_mode_change(old_mode, new_mode)`: whether the ball has just left warp.
fn warp_ended(args: &[Value]) -> bool {
    args.first().and_then(Value::as_int) == Some(DESTINY_MODE_WARP)
}

/// Blocks the tree from being reset until the ship has left warp, suspended.
#[derive(Debug, Default)]
pub struct BlockWaitUntilWarpComplete;

impl Task for BlockWaitUntilWarpComplete {
    fn type_name(&self) -> &'static str {
        "BlockWaitUntilWarpComplete"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        blocking::on_enter(t);
        if let Some(me) = t.my_item_id() {
            t.subscribe_item(me, message::ON_BALL_MODE_CHANGED, HANDLER_DESTINY_MODE_CHANGE);
        }
        t.set_status_to_suspended();
    }

    fn on_exit(&mut self, t: &mut TaskCtx<'_, '_>) {
        blocking::on_exit(t);
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if warp_ended(args) {
            if let Some(me) = t.my_item_id() {
                t.unsubscribe_item(me, message::ON_BALL_MODE_CHANGED, HANDLER_DESTINY_MODE_CHANGE);
            }
            t.set_status_to_success();
            t.on_status_updated();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            if let Some(me) = t.my_item_id() {
                t.unsubscribe_item(me, message::ON_BALL_MODE_CHANGED, HANDLER_DESTINY_MODE_CHANGE);
            }
            t.set_status_to_invalid();
        }
    }
}

/// Waits (blocked) until the ship has left warp.
#[derive(Debug, Default)]
pub struct WaitUntilWarpComplete;

impl Task for WaitUntilWarpComplete {
    fn type_name(&self) -> &'static str {
        "WaitUntilWarpComplete"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_running();
        if let Some(me) = t.my_item_id() {
            t.subscribe_item(me, message::ON_BALL_MODE_CHANGED, HANDLER_DESTINY_MODE_CHANGE);
        }
        t.set_status_to_blocked();
    }

    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if warp_ended(args) {
            if let Some(me) = t.my_item_id() {
                t.unsubscribe_item(me, message::ON_BALL_MODE_CHANGED, HANDLER_DESTINY_MODE_CHANGE);
            }
            t.set_status_to_success();
            t.start_self_next_tick();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            if let Some(me) = t.my_item_id() {
                t.unsubscribe_item(me, message::ON_BALL_MODE_CHANGED, HANDLER_DESTINY_MODE_CHANGE);
            }
            t.set_status_to_invalid();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::blackboards::{Address, ScopeType};
    use crate::env::testing::TestGroup;
    use crate::env::{Formation, SlimItem};
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn rig() -> Rig {
        let mut rig = Rig::new();
        rig.world.groups.insert(10, TestGroup { members: vec![1, 2], ..Default::default() });
        rig.post(ScopeType::Item, "Where", Value::from_vec3([1.0, 2.0, 3.0]));
        rig
    }

    fn warp_attrs() -> Attrs {
        Attrs::new()
            .with("destinationCoordinatesAddress", item("Where"))
            .with("additionalFleetMembersAddress", item("More"))
            .with("formationType", 2i64)
            .with("formationSpacing", 500i64)
            .with("formationSize", 10i64)
    }

    #[test]
    fn the_members_and_the_ships_that_are_added_warp_in_formation_players_and_npcs_alike() {
        let mut rig = rig();
        rig.world
            .slims
            .insert(30, SlimItem { item_id: 30, owner_id: Some(90_000_100), ..Default::default() });
        rig.world.slims.insert(31, SlimItem { item_id: 31, owner_id: Some(1_000_125), ..Default::default() });
        rig.world
            .slims
            .insert(32, SlimItem { item_id: 32, owner_id: Some(150_000_000), ..Default::default() });
        rig.world.pre_inv64_characters.insert(150_000_000);
        rig.world.add_ball(2, [0.0; 3], 600);
        rig.world.npc_warpers.insert(2);
        rig.post(
            ScopeType::Item,
            "More",
            Value::set([Value::Int(1), Value::Int(30), Value::Int(31), Value::Int(32), Value::Int(99)]),
        );
        rig.world.formation = Some(Formation {
            destination_coordinates: vec![
                (1, [10.0; 3]),
                (2, [20.0; 3]),
                (30, [30.0; 3]),
                (31, [40.0; 3]),
                (32, [50.0; 3]),
            ],
            jitter_radius: 300.0,
            minimum_range: 1000.0,
            min_warp_speed: 3.0,
            fleet_candidates: vec![
                (1, None),
                (2, None),
                (30, Some(90_000_100)),
                (31, None),
                (32, Some(150_000_000)),
            ],
        });
        rig.world.add_ball(1, [0.0; 3], 600);
        assert_eq!(rig.run(Box::new(FormationWarpGroup), warp_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        let calls = rig.world.take_calls();
        assert_eq!(
            calls[0],
            "pick_formation([(1, None), (2, None), (30, Some(90000100)), (31, None), (32, Some(150000000))], Tuple([Float(1.0), Float(2.0), Float(3.0)]), FormationSettings { formation_type: Int(2), formation_spacing: Int(500), formation_size: Int(10) })"
        );
        assert_eq!(
            &calls[1..],
            [
                // ship 1 has a ball that cannot warp as an NPC, ship 2 warps as one
                "npc_warp_to(2, [20.0, 20.0, 20.0], 300, 1000, 3)",
                // players: asked if they are busy twice, as the client does
                "cancel_if_doing_stuff(30)",
                "cancel_if_doing_stuff(30)",
                "warp_to(30, [30.0, 30.0, 30.0], 1000, 3, 300)",
                "cancel_if_doing_stuff(32)",
                "cancel_if_doing_stuff(32)",
                "warp_to(32, [50.0, 50.0, 50.0], 1000, 3, 300)",
            ]
        );
    }

    #[test]
    fn a_player_who_is_busy_does_not_warp() {
        let mut rig = rig();
        rig.world.busy_ships.insert(30);
        rig.world.formation = Some(Formation {
            destination_coordinates: vec![(30, [30.0; 3])],
            fleet_candidates: vec![(30, Some(90_000_100))],
            ..Default::default()
        });
        assert_eq!(rig.run(Box::new(FormationWarpGroup), warp_attrs()), Status::Success);
        let calls = rig.world.take_calls();
        assert_eq!(calls.iter().filter(|c| c.starts_with("warp_to")).count(), 0);
        assert_eq!(calls.iter().filter(|c| c.starts_with("cancel_if_doing_stuff")).count(), 1);
    }

    #[test]
    fn a_group_without_members_raises_and_a_ship_without_a_destination_raises() {
        let mut rig = rig();
        rig.world.groups.insert(10, TestGroup::default());
        rig.run(Box::new(FormationWarpGroup), warp_attrs());
        assert!(matches!(&rig.last_error, Some(BehaviorError::Task(m)) if m.contains("IndexError")));
        let mut rig = self::rig();
        rig.world.formation = Some(Formation { fleet_candidates: vec![(1, None)], ..Default::default() });
        rig.run(Box::new(FormationWarpGroup), warp_attrs());
        assert!(matches!(&rig.last_error, Some(BehaviorError::World(crate::env::WorldError::Key(_)))));
    }

    #[test]
    fn the_fleet_is_aimed_at_the_place_at_full_speed_and_a_member_that_goes_wrong_is_only_logged() {
        let mut rig = rig();
        rig.world.slims.insert(30, SlimItem { item_id: 30, ..Default::default() });
        rig.world.failing_balls.insert(1);
        rig.post(ScopeType::Item, "More", Value::set([Value::Int(30), Value::Int(99)]));
        assert_eq!(rig.run(Box::new(AlignFleet), warp_attrs()), Status::Success);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.world.take_calls(),
            [
                "goto_point(1, [1.0, 2.0, 3.0])",
                "goto_point(2, [1.0, 2.0, 3.0])",
                "set_speed_fraction(2, 1)",
                "goto_point(30, [1.0, 2.0, 3.0])",
                "set_speed_fraction(30, 1)",
            ]
        );
    }

    fn mode_change(rig: &mut Rig, old: i64, new: i64) {
        rig.send_event(1, message::ON_BALL_MODE_CHANGED, &[Value::Int(old), Value::Int(new)]);
    }

    #[test]
    fn a_task_that_waits_for_the_warp_to_end_succeeds_when_the_ball_leaves_warp() {
        let mut rig = Rig::new();
        let task = rig.tree.add_task(Box::new(WaitUntilWarpComplete), Attrs::new());
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(task), Status::Blocking);
        mode_change(&mut rig, 0, 3);
        assert_eq!(
            rig.tree.status_of(task),
            Status::Blocking,
            "a ball that goes into warp is not one that leaves it"
        );
        mode_change(&mut rig, DESTINY_MODE_WARP, 0);
        assert_eq!(rig.tree.status_of(task), Status::Success);
        mode_change(&mut rig, DESTINY_MODE_WARP, 0);
    }

    #[test]
    fn a_blocking_task_that_waits_for_the_warp_to_end_holds_the_reset_until_then() {
        let mut rig = Rig::new();
        let task = rig.tree.add_task(Box::new(BlockWaitUntilWarpComplete), Attrs::new());
        rig.tree.start_root_task(task);
        rig.tick().unwrap();
        assert_eq!(rig.tree.status_of(task), Status::Suspended);
        assert!(rig.tree.is_reset_blocked());
        mode_change(&mut rig, DESTINY_MODE_WARP, 0);
        assert_eq!(rig.tree.status_of(task), Status::Success);
        assert!(!rig.tree.is_reset_blocked());
    }
}
