//! `behaviors/actions/reinforcements.py`.

use crate::blackboards::ScopeType;
use crate::constants::blackboardchannels::unspawn_entity;
use crate::env::ReinforcementWave;
use crate::groups::mixin::{get_group_owner_id, get_member_id_list};
use crate::tree::{Task, TaskCtx};
use crate::utility::blackboards::get_response_thresholds;
use crate::utility::security::get_spawnlist_by_group_owner_and_security_level;
use crate::value::Value;

enter_task!(
    /// Calls in the group's reinforcements when they are authorized and available.
    CallInReinforcementsAction,
    |t| {
        t.set_status_to_failed();
        let Some(group_id) = t.ctx().my_entity_group_id else {
            t.raise(crate::env::WorldError::Key("myEntityGroupId".to_string()).into());
            return;
        };
        let authorized = t.world().is_reinforcement_authorized(group_id);
        let Some(authorized) = t.ok(authorized) else { return };
        if !authorized || !t.world().are_reinforcements_available(group_id) {
            return;
        }
        let thresholds = get_response_thresholds(t);
        t.world().call_in_reinforcements(group_id, thresholds);
        t.set_status_to_success();
    }
);

#[derive(Debug, Default)]
pub struct AddReinforcementsByOwnerId;

impl Task for AddReinforcementsByOwnerId {
    fn type_name(&self) -> &'static str {
        "AddReinforcementsByOwnerId"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(group_id) = t.ctx().my_entity_group_id else {
            t.raise(crate::env::WorldError::Key("myEntityGroupId".to_string()).into());
            return;
        };
        if t.world().reinforced_group_exists(group_id) {
            return;
        }
        let spawnlist = get_spawnlist_by_group_owner_and_security_level(t);
        if t.faulted() {
            return;
        }
        if !spawnlist.truthy() {
            t.world().log_error(
                "Unable to add reinforcements: the owner of the group has no reinforcements defined",
            );
            return;
        }
        let (Some(cooldown), Some(threshold)) =
            (t.required("reinforcementWaveCooldownSeconds"), t.required("reinforcementThreshold"))
        else {
            return;
        };
        let behavior_override_id = if t.has_attribute("behaviorOverrideId") {
            Some(t.attribute("behaviorOverrideId"))
        } else {
            None
        };
        let entity_count = get_member_id_list(t).len();
        let owner_id = get_group_owner_id(t);
        if t.faulted() {
            return;
        }
        let wave = ReinforcementWave {
            wave_id: spawnlist,
            cooldown_seconds: cooldown,
            threshold,
            behavior_override_id,
        };
        t.world().add_entity_group_reinforcement(group_id, entity_count, owner_id, wave);
        t.set_status_to_success();
    }
}

enter_task!(
    /// Tells the reinforcements of my group to unspawn and forgets them.
    CallForUnspawningReinforcements,
    |t| {
        let Some(group_id) = t.ctx().my_entity_group_id else {
            t.raise(crate::env::WorldError::Key("myEntityGroupId".to_string()).into());
            return;
        };
        let entity_ids = t.world().active_reinforced_entity_ids(group_id);
        let channel_name = unspawn_entity().name;
        debug_assert_eq!(unspawn_entity().scope_type, ScopeType::Item);
        for entity_id in entity_ids {
            // whatever goes wrong for one entity is not this task's error
            t.eat_exceptions(|t| {
                let channel = t.channel_for_item(entity_id, &channel_name);
                t.channel_send(&channel, Value::Bool(true));
            });
        }
        t.world().remove_active_reinforced_entity_ids(group_id);
        t.set_status_to_success();
    }
);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::ResponseThresholds;
    use crate::env::testing::TestGroup;
    use crate::tree::tests::Rig;
    use crate::tree::{Attrs, Status};

    #[test]
    fn reinforcements_are_called_in_when_they_are_authorized_and_available() {
        for (authorized, available, wanted) in
            [(true, true, Status::Success), (true, false, Status::Failure), (false, true, Status::Failure)]
        {
            let mut rig = Rig::new();
            rig.world.reinforcement_authorized.insert(10, Ok(authorized));
            if available {
                rig.world.reinforcements_available.insert(10);
            }
            rig.world.thresholds = Some(ResponseThresholds {
                hostile_response_threshold: -1.0,
                friendly_response_threshold: 1.0,
            });
            assert_eq!(rig.run(Box::new(CallInReinforcementsAction), Attrs::new()), wanted);
            assert_eq!(rig.world.take_calls().len(), usize::from(wanted == Status::Success));
        }
    }

    #[test]
    fn a_group_with_a_wave_for_its_owner_is_reinforced_once() {
        let attrs = Attrs::new()
            .with("reinforcementWaveCooldownSeconds", 60i64)
            .with("reinforcementThreshold", 0.5)
            .with(
                "highSecOwnerToSpawnlistMapping",
                Value::Dict(vec![(Value::Int(1000006), Value::Int(4321))]),
            );
        let mut rig = Rig::new();
        rig.ctx.solar_system_id = Some(30000142);
        rig.world.security.insert(30000142, 0.9);
        rig.world
            .groups
            .insert(10, TestGroup { members: vec![1, 2], owner_id: Some(1000006), ..Default::default() });
        assert_eq!(rig.run(Box::new(AddReinforcementsByOwnerId), attrs.clone()), Status::Success);
        assert_eq!(rig.last_error, None);
        assert_eq!(
            rig.world.take_calls(),
            ["add_entity_group_reinforcement(10, 2, Some(1000006), 4321, 60, 0.5, None)"]
        );
        rig.world.reinforced_groups.insert(10);
        assert_eq!(
            rig.run(Box::new(AddReinforcementsByOwnerId), attrs.clone()),
            Status::Failure,
            "already reinforced"
        );
        let mut rig = Rig::new();
        rig.ctx.solar_system_id = Some(30000142);
        rig.world.security.insert(30000142, 0.9);
        rig.world.groups.insert(10, TestGroup { members: vec![1], owner_id: Some(5), ..Default::default() });
        assert_eq!(
            rig.run(Box::new(AddReinforcementsByOwnerId), attrs),
            Status::Failure,
            "no wave for this owner"
        );
        assert!(rig.world.take_calls()[0].starts_with("log_error("));
    }

    #[test]
    fn the_reinforcements_are_told_to_unspawn_and_forgotten() {
        let mut rig = Rig::new();
        rig.world.reinforced.insert(10, vec![70, 71]);
        assert_eq!(rig.run(Box::new(CallForUnspawningReinforcements), Attrs::new()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["remove_active_reinforced_entity_ids(10)"]);
        let mut cx = crate::tree::Cx {
            core: &mut rig.tree.core,
            ctx: &mut rig.ctx,
            shared: &mut rig.shared,
            world: &mut rig.world,
        };
        assert_eq!(
            cx.last_value_of_channel(crate::blackboards::item_scope(70), "UNSPAWN_ENTITY"),
            Value::Bool(true)
        );
    }
}
