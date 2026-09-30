//! `behaviors/const/blackboardchannels.py`: the names of the channels the tasks and the server share, and the addresses built from them.

use crate::blackboards::{Address, ScopeType};

pub const ENTITY_GROUP_IDS_BY_FLEET_TYPE_MESSAGE_CHANNEL: &str = "ENTITY_GROUP_IDS_BY_FLEET_TYPE";
pub const GUARD_OBJECT_ITEM_ID_MESSAGE_CHANNEL: &str = "GUARD_OBJECT_ITEM_ID";
pub const ROLE_MESSAGE_CHANNEL: &str = "ROLE";
pub const SPAWN_POSITION_MESSAGE_CHANNEL: &str = "SPAWN_POSITION";
pub const STANDINGS_FRIENDLY_THRESHOLD_CHANNEL_NAME: &str = "STANDINGS_FRIENDLY_THRESHOLD";
pub const STANDINGS_HOSTILE_THRESHOLD_CHANNEL_NAME: &str = "STANDINGS_HOSTILE_THRESHOLD";
pub const UNDOCK_DIRECTION_CHANNEL_NAME: &str = "UNDOCK_DIRECTION";
pub const DYNAMIC_NPC_SPAWN_NPCS: &str = "DYNAMIC_NPC_SPAWN_NPCS";
pub const DYNAMIC_NPC_SPAWN_OWNER_ID: &str = "DYNAMIC_NPC_SPAWN_OWNER_ID";
pub const DYNAMIC_NPC_SPAWN_GROUP_BEHAVIOR_ID: &str = "DYNAMIC_NPC_SPAWN_GROUP_BEHAVIOR";
pub const DYNAMIC_NPC_SPAWN_OBJECT_ID: &str = "DYNAMIC_NPC_SPAWN_OBJECT_ID";
pub const DYNAMIC_NPC_SPAWN_DISTANCE: &str = "DYNAMIC_NPC_SPAWN_DISTANCE";
pub const DYNAMIC_NPC_SPAWN_ITEM_ID: &str = "DYNAMIC_NPC_SPAWN_ITEM_ID";
pub const GUARD_OBJECT_POSITION_CHANNEL_NAME: &str = "GUARD_OBJECT_POSITION";
pub const GUARD_OBJECT_ORBIT_RANGE_CHANNEL_NAME: &str = "GUARD_OBJECT_ORBIT_RANGE";
pub const DRONE_CONTROLLER_CHANNEL_NAME: &str = "DRONE_CONTROLLER";
pub const GROUP_PRIMARY_TARGET_CHANNEL_NAME: &str = "GROUP_PRIMARY_TARGET";
pub const CAN_SPAWN_SITES_CHANNEL_NAME: &str = "CAN_SPAWN_SITES";
pub const IS_DUNGEON_INDEPENDENT_CHANNEL_NAME: &str = "IS_DUNGEON_INDEPENDENT";
pub const EXTRA_FLEET_MEMBERS_CHANNEL_NAME: &str = "Extra Fleet Members";

/// `REINFORCEMENTS_AUTHORIZED` of the server's reinforcement manager (its package is not in the client extract; the name is this crate's).
pub const REINFORCEMENTS_AUTHORIZED: &str = "REINFORCEMENTS_AUTHORIZED";
/// `REINFORCEMENT_COOLDOWN`, likewise.
pub const REINFORCEMENT_COOLDOWN: &str = "REINFORCEMENT_COOLDOWN";

macro_rules! address {
    ($(#[$doc:meta])* $name:ident, $scope:ident, $channel:expr) => {
        $(#[$doc])*
        pub fn $name() -> Address {
            Address::new(ScopeType::$scope, $channel)
        }
    };
}

address!(additional_loot_tables_address, EntityGroup, "ADDITIONAL_LOOT_TABLES");
address!(combat_targets_set_address, EntityGroup, "COMBAT_TARGETS_SET");
address!(combat_min_optimal_address, Item, "COMBAT_MIN_OPTIMAL_RANGE");
address!(combat_min_falloff_address, Item, "COMBAT_MIN_FALLOFF_RANGE");
address!(commander_address, EntityGroup, "COMMANDER");
address!(drone_controller_address, EntityGroup, "DRONE_CONTROLLER");
address!(
    self_destruct_drone_on_controller_death_address,
    EntityGroup,
    "SELF_DESTRUCT_DRONE_ON_CONTROLLER_DEATH"
);
address!(guard_object_aggression_range, EntityGroup, "GUARD_OBJECT_AGGRESSION_RANGE");
address!(guard_object_item_id, EntityGroup, GUARD_OBJECT_ITEM_ID_MESSAGE_CHANNEL);
address!(guard_object_position, EntityGroup, GUARD_OBJECT_POSITION_CHANNEL_NAME);
address!(guard_object_orbit_range, EntityGroup, GUARD_OBJECT_ORBIT_RANGE_CHANNEL_NAME);
address!(group_primary_target, EntityGroup, GROUP_PRIMARY_TARGET_CHANNEL_NAME);
address!(hauler_pick_up_ball_ids, EntityGroup, "HAULER_PICK_UP_BALLS");
address!(hauler_requested_address, EntityGroup, "HAULER_REQUESTED");
address!(last_reinforcement_request_coordinate_address, EntityGroup, "LAST_REINFORCEMENT_REQUEST_COORDINATE");
address!(my_combat_target, Item, "MY_COMBAT_TARGET");
address!(reinforcement_requested, EntityGroup, "REINFORCEMENT_REQUESTED");
address!(standings_friendly_threshold_address, EntityGroup, STANDINGS_FRIENDLY_THRESHOLD_CHANNEL_NAME);
address!(standings_hostile_threshold_address, EntityGroup, STANDINGS_HOSTILE_THRESHOLD_CHANNEL_NAME);
address!(unspawn_entity, Item, "UNSPAWN_ENTITY");
address!(warp_scrambled_address, Item, "WARP_SCRAMBLED");
address!(reinforcements_authorized_address, EntityGroup, REINFORCEMENTS_AUTHORIZED);
address!(reinforcement_cooldown_address, EntityGroup, REINFORCEMENT_COOLDOWN);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_address_is_the_scope_and_the_channel() {
        assert_eq!(commander_address(), Address::new(ScopeType::EntityGroup, "COMMANDER"));
        assert_eq!(guard_object_item_id().name, GUARD_OBJECT_ITEM_ID_MESSAGE_CHANNEL);
        assert_eq!(my_combat_target().scope_type, ScopeType::Item);
    }
}
