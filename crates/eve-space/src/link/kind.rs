pub const HELLO: u8 = 1;
pub const HELLO_ACK: u8 = 2;
pub const ENTER: u8 = 3;
pub const LEAVE: u8 = 4;
pub const BIND: u8 = 5;
pub const UPDATE_STATE: u8 = 6;
pub const COMMAND: u8 = 7;
pub const SHIP_SPEC: u8 = 8;
pub const NOTIFY: u8 = 9;
pub const DOCKED: u8 = 10;
pub const REFUSED: u8 = 11;
pub const ACCEPTED: u8 = 12;
pub const ENTER_REPLY: u8 = 13;
pub const APPROACHING: u8 = 14;
pub const JUMPING: u8 = 15;
pub const JUMPED: u8 = 16;
pub const TARGETS: u8 = 17;
pub const TARGET_REFUSED: u8 = 18;
pub const ACTIVATE: u8 = 19;
pub const CONDITION: u8 = 20;
pub const DESTROYED: u8 = 21;
pub const SPAWN: u8 = 22;
pub const SPAWN_REPLY: u8 = 23;
pub const DESTROY: u8 = 24;
pub const RESPAWN: u8 = 25;
pub const PLACE_WRECK: u8 = 26;
pub const CHARGE_SPENT: u8 = 27;
pub const PLACE_CONTAINER: u8 = 28;
pub const CONTAINER_PLACED: u8 = 29;
pub const CHANGE_WRECK: u8 = 30;
pub const REMOVE_WRECK: u8 = 31;
pub const MEASURED: u8 = 32;
pub const ADJUST_CHARGE: u8 = 33;
pub const CHARGE_ADJUSTED: u8 = 34;
pub const POSITION: u8 = 50;
pub const ARRIVED: u8 = 35;
pub const LAUNCH_DRONE: u8 = 36;
pub const DRONE_COMMAND: u8 = 37;
pub const DRONE_RESULTS: u8 = 38;
pub const DRONE_SCOOPED: u8 = 39;
pub const DRONE_RELEASED: u8 = 40;
pub const PLACE_ASTEROID: u8 = 45;
pub const DRONE_WORK_CYCLE: u8 = 46;
pub const DRONE_WORK_RESULT: u8 = 47;
pub const REMOTE_REPAIR_CYCLE: u8 = 48;
pub const REMOTE_REPAIR_CYCLE_RESULT: u8 = 49;
pub const MODULE_WORK_CYCLE: u8 = 51;
pub const MODULE_WORK_RESULT: u8 = 52;
pub const MODULE_ENDED: u8 = 53;
pub const HEAT_PROFILE: u8 = 41;
pub const MODULE_OVERHEAT: u8 = 42;
pub const MODULE_OFFLINE: u8 = 43;
pub const RACK_OVERHEAT: u8 = 44;
pub const LAUNCH_FIGHTER: u8 = 54;
pub const FIGHTER_ORDER: u8 = 55;
pub const FIGHTER_RESULTS: u8 = 56;
pub const FIGHTER_LANDED: u8 = 57;
pub const FIGHTER_RELEASED: u8 = 58;
pub const FIGHTER_GOTO_POINT: u8 = 59;
pub const FIGHTER_SIZE: u8 = 60;
pub const UPDATE_EFFECT: u8 = 61;
/// A deployable whose record the host keeps (a cynosural field): the simulation says it was lit, and the host puts it back after a restart (protocol 44).
pub const DEPLOYED: u8 = 63;
/// The host takes a deployable out of space when its record's time is up (protocol 44).
pub const REMOVE_DEPLOYED: u8 = 64;
/// The host puts a scanner probe in space where its pilot launched it (protocol 45).
pub const PLACE_PROBE: u8 = 65;
/// The host takes a scanner probe out of space (recalled, or its time is up; protocol 45).
pub const REMOVE_PROBE: u8 = 66;
/// The host puts a mobile warp disruptor in space where its pilot dropped it, anchored or not (protocol 48).
pub const PLACE_DISRUPTOR: u8 = 68;
/// The host says a mobile warp disruptor is anchored (its disruption on) or not (protocol 48).
pub const SET_DISRUPTOR_ACTIVE: u8 = 69;
/// The host puts a Mobile Cynosural Inhibitor in space, anchored or not (protocol 50): the same placement as a disruptor's.
pub const PLACE_INHIBITOR: u8 = 70;
/// The host puts a Mobile Cynosural Beacon in space (protocol 58); the placement is a disruptor's (range 0).
pub const PLACE_CYNO_BEACON: u8 = 73;
/// The host puts an Upwell structure in space, as a body of the system with its slim state (protocol 59).
pub const PLACE_STRUCTURE: u8 = 74;
/// The host takes a structure out of space (it was unanchored or destroyed; protocol 59).
pub const REMOVE_STRUCTURE: u8 = 75;
/// The host changes a structure's slim state (state, timer, upkeep, unanchoring, repairing; protocol 59).
pub const SET_STRUCTURE_STATE: u8 = 76;
/// The simulation says a hit emptied a layer of a structure or paused its repair (simulation to host, protocol 60).
pub const STRUCTURE_DAMAGED: u8 = 77;
/// A CRAB the host keeps a record of is put in space (protocol 61).
pub const PLACE_ANALYSIS_BEACON: u8 = 78;
/// The host sets a CRAB's state (protocol 61).
pub const SET_ANALYSIS_BEACON_STATE: u8 = 79;
/// The host attaches or releases a controlled-structure observer (protocol 64).
pub const CONTROL_STRUCTURE: u8 = 84;
/// The scan targets of a system, answered to the host's scan target query (command 27, protocol 52).
pub const SCAN_TARGETS: u8 = 71;
/// The things a directional scan can see in a cone of space, answered to command 28 (protocol 54).
pub const DIRECTIONAL_TARGETS: u8 = 72;
pub const BUFF_STATE: u8 = 62;
pub const ECM_BURST: u8 = 67;
/// Host permissions and deadlines for a pilot's tether (protocol 62).
pub const TETHER_POLICY: u8 = 80;
/// Simulation tether transition sent to the host (protocol 62).
pub const TETHER_CHANGED: u8 = 81;
/// Simulation-to-host damageable candidates for one structure pulse or ordered chain hop (protocol 68).
pub const STRUCTURE_DAMAGE_CANDIDATES_REQUEST: u8 = 85;
/// Host-to-simulation allowed targets for exactly the same correlated damage step (protocol 68).
pub const STRUCTURE_DAMAGE_CANDIDATES_RESULT: u8 = 86;
/// The host removes a structure-owned cyno beacon from its solar-system scene (protocol 68).
pub const REMOVE_CYNO_BEACON: u8 = 87;
/// The simulation registers or updates a created damageable entity's owner identity (protocol 69).
pub const DAMAGEABLE_ENTITY_UPSERTED: u8 = 88;
/// The simulation removes a non-destroyed damageable entity from the Host identity registry (protocol 69).
pub const DAMAGEABLE_ENTITY_REMOVED: u8 = 89;
/// The host creates or refreshes a static-free Abyssal room scene (protocol 71).
pub const ENSURE_ABYSSAL_SCENE: u8 = 90;
/// Result of ensuring an Abyssal room scene (protocol 71).
pub const ENSURE_ABYSSAL_SCENE_REPLY: u8 = 91;
/// The host moves a resident participant and its live state between Abyssal rooms (protocol 71).
pub const MOVE_ABYSSAL_PARTICIPANT: u8 = 92;
/// Result of moving an Abyssal participant (protocol 71).
pub const MOVE_ABYSSAL_PARTICIPANT_REPLY: u8 = 93;
/// The host rebinds a new session to a resident Abyssal ship (protocol 71).
pub const REBIND_ABYSSAL_SESSION: u8 = 94;
/// Result of rebinding a session to an Abyssal ship (protocol 71).
pub const REBIND_ABYSSAL_SESSION_REPLY: u8 = 95;
/// The host discards an empty private Abyssal room scene (protocol 71).
pub const REMOVE_ABYSSAL_SCENE: u8 = 96;
/// Result of removing an Abyssal scene (protocol 71).
pub const REMOVE_ABYSSAL_SCENE_REPLY: u8 = 97;
/// The host materializes and publishes one persisted public Abyssal trace (protocol 71).
pub const ENSURE_ABYSSAL_TRACE_PUBLISHED: u8 = 98;
/// Result of materializing and publishing an Abyssal trace (protocol 71).
pub const ENSURE_ABYSSAL_TRACE_PUBLISHED_REPLY: u8 = 99;
/// The host removes one public Abyssal trace after its Domain/item cleanup (protocol 71).
pub const REMOVE_ABYSSAL_TRACE: u8 = 100;
/// Result of removing a public Abyssal trace (protocol 71).
pub const REMOVE_ABYSSAL_TRACE_REPLY: u8 = 101;
/// The host settles a resident Abyssal participant without requiring its client session (protocol 71).
pub const REMOVE_ABYSSAL_PARTICIPANT: u8 = 102;
/// Result of removing a resident Abyssal participant (protocol 71).
pub const REMOVE_ABYSSAL_PARTICIPANT_REPLY: u8 = 103;
/// The Host reads a resident participant's authoritative Abyssal pose (protocol 71).
pub const GET_ABYSSAL_PARTICIPANT_POSE: u8 = 104;
/// Result of reading an Abyssal participant's authoritative pose (protocol 71).
pub const GET_ABYSSAL_PARTICIPANT_POSE_REPLY: u8 = 105;
/// Host request to ensure a static conduit exists as an isolated Abyssal scene body (protocol 71).
pub const ENSURE_ABYSSAL_SCENE_OBJECT: u8 = 106;
/// Result of ensuring a static Abyssal scene body (protocol 71).
pub const ENSURE_ABYSSAL_SCENE_OBJECT_REPLY: u8 = 107;
pub const ENSURE_ABYSSAL_REWARD_CACHE: u8 = 108;
pub const ENSURE_ABYSSAL_REWARD_CACHE_REPLY: u8 = 109;
pub const ENSURE_ABYSSAL_LOOT_CONTAINER: u8 = 110;
pub const ENSURE_ABYSSAL_LOOT_CONTAINER_REPLY: u8 = 111;
pub const ENSURE_ABYSSAL_ENVIRONMENT_OBJECT: u8 = 112;
pub const ENSURE_ABYSSAL_ENVIRONMENT_OBJECT_REPLY: u8 = 113;
pub const ABYSSAL_FIELD_MEMBERSHIP: u8 = 114;
pub const REFRESH_ABYSSAL_NPC: u8 = 115;
pub const REFRESH_ABYSSAL_NPC_REPLY: u8 = 116;

/// The host puts a wormhole end in space (protocol 63).
pub const PLACE_WORMHOLE: u8 = 82;
/// The host takes a wormhole end out of space (protocol 63).
pub const REMOVE_WORMHOLE: u8 = 83;
