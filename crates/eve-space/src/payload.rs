//! The destiny stream's payloads: the state buffer, `AddBalls2`, `SetState`, and the actions a command is answered with.

use eve_destiny::{BallEntity, BallError, encode_entity_ball};
use eve_marshal::Value;

use crate::health::DamageState;
use crate::ship::Ship;
use crate::slim;
use crate::wire::{filetime, id, int, key_val, list, real, slim_object, text, tuple, word};
use crate::world::{BodyKind, SolarSystem};
use crate::wreck::{DESTRUCTION_EFFECT_EXPLOSION, Wreck};

/// The kind byte of a state buffer: 0 for `SetState`, 1 for `AddBalls2`.
const SET_STATE: u8 = 0;
const ADD_BALLS: u8 = 1;

/// The shield fraction, its recharge and armor and structure fractions of something that can be hurt: `((shield, tau, time), armor, structure)`.
pub fn damage_state(state: &DamageState, now: i64) -> Value {
    tuple([
        tuple([real(state.shield), real(state.shield_tau_ms), filetime(now)]),
        real(state.armor),
        real(state.structure),
    ])
}

/// A station always answers with the same recharge, whatever it is.
pub(crate) const STATION_SHIELD_TAU_MS: f64 = 110_000.0;

fn state_buffer(kind: u8, stamp: u32, balls: &[BallEntity]) -> Result<Value, BallError> {
    let mut bytes = vec![kind];
    bytes.extend_from_slice(&stamp.to_le_bytes());
    for ball in balls {
        bytes.extend_from_slice(&encode_entity_ball(ball)?);
    }
    Ok(Value::Buffer(bytes.into()))
}

/// The state buffer of an `AddBalls2` for these balls (the missile that is fired has no slim item of a ship, so it builds its own update).
pub fn add_balls_buffer(stamp: u32, balls: &[BallEntity]) -> Result<Value, BallError> {
    state_buffer(ADD_BALLS, stamp, balls)
}

/// One action of a `DoDestinyUpdate`: what happened at a stamp, `(stamp, (name, args))`.
pub fn action(stamp: u32, name: &'static str, args: impl IntoIterator<Item = Value>) -> Value {
    tuple([id(i64::from(stamp)), tuple([word(name), tuple(args)])])
}

/// The arguments of a `DoDestinyUpdate` notification: the actions and whether the client should wait for them.
pub fn destiny_update(actions: impl IntoIterator<Item = Value>, wait: bool) -> Vec<Value> {
    vec![list(actions), Value::Bool(wait)]
}

/// The ship's slim item as a plain dictionary (what `AddBalls2` carries).
fn ship_dict(ship: &Ship) -> Value {
    Value::Dict(slim::ship_entries(ship).into_iter().map(|(k, v)| (word(k), v)).collect())
}

/// `AddBalls2` for the ship alone: the buffer with its ball, and the slim item with the damage state.
pub fn add_ship(stamp: u32, ship: &Ship, now: i64) -> Result<Value, BallError> {
    add_ships(stamp, &[ship], now)
}

/// `AddBalls2` for several ships: the buffer with their balls, and their slim items with the damage state of each, in the order given.
pub fn add_ships(stamp: u32, ships: &[&Ship], now: i64) -> Result<Value, BallError> {
    let balls: Vec<BallEntity> = ships.iter().map(|ship| ship.ball()).collect();
    let buffer = state_buffer(ADD_BALLS, stamp, &balls)?;
    let extras =
        ships.iter().map(|ship| tuple([ship_dict(ship), damage_state(&ship.health.state_at(now), now)]));
    Ok(action(stamp, "AddBalls2", [tuple([buffer, list(extras)])]))
}

/// What the client is told of a wreck's damage: nothing to hurt, so no shield, no armor, a whole hull.
fn wreck_damage_state(now: i64) -> Value {
    damage_state(&DamageState { shield: 0.0, shield_tau_ms: 0.0, armor: 0.0, structure: 1.0 }, now)
}

/// `AddBalls2` for a wreck: the buffer with its ball, and its slim item with the damage state.
pub fn add_wreck(stamp: u32, wreck: &Wreck, now: i64) -> Result<Value, BallError> {
    let buffer = state_buffer(ADD_BALLS, stamp, &[wreck.ball()])?;
    // as a ship's, the slim item of an `AddBalls2` is a plain dictionary (a `SetState` sends objects)
    let slim = Value::Dict(wreck.slim_entries().into_iter().map(|(k, v)| (word(k), v)).collect());
    Ok(action(stamp, "AddBalls2", [tuple([buffer, list([tuple([slim, wreck_damage_state(now)])])])]))
}

/// `AddBalls2` for a structure body of the system (the body at `index`): its ball, and its slim item with the damage state.
pub fn add_structure(stamp: u32, system: &SolarSystem, index: usize, now: i64) -> Result<Value, BallError> {
    let buffer = state_buffer(ADD_BALLS, stamp, &[rigid_ball(system, index)])?;
    let slim = Value::Dict(
        slim::body_entries(system, &system.bodies[index]).into_iter().map(|(k, v)| (word(k), v)).collect(),
    );
    let damage = damage_state(&DamageState::whole(STATION_SHIELD_TAU_MS), now);
    Ok(action(stamp, "AddBalls2", [tuple([buffer, list([tuple([slim, damage])])])]))
}

/// `AddBalls2` for each structure of the system (the customs offices too): a pilot who comes in is told them as balls, since a structure placed while
/// nobody was in the system was told to nobody.
pub fn add_structures(stamp: u32, system: &SolarSystem, now: i64) -> Vec<Value> {
    (0..system.bodies.len())
        .filter(|&index| system.bodies[index].kind == BodyKind::Structure)
        .filter_map(|index| add_structure(stamp, system, index, now).ok())
        .collect()
}

/// `AddBalls2` for a wormhole end of the system (the body at `index`): its ball, and its slim item with an empty damage state (a wormhole has no hit points).
pub fn add_wormhole(stamp: u32, system: &SolarSystem, index: usize, now: i64) -> Result<Value, BallError> {
    let buffer = state_buffer(ADD_BALLS, stamp, &[rigid_ball(system, index)])?;
    let slim = Value::Dict(
        slim::body_entries(system, &system.bodies[index]).into_iter().map(|(k, v)| (word(k), v)).collect(),
    );
    Ok(action(stamp, "AddBalls2", [tuple([buffer, list([tuple([slim, wreck_damage_state(now)])])])]))
}

/// `OnSlimItemChange` for a structure body: its slim item is told again, as an object, to whoever has it in view.
pub fn structure_slim_change(stamp: u32, system: &SolarSystem, index: usize) -> Value {
    let body = &system.bodies[index];
    action(stamp, "OnSlimItemChange", [id(body.id), slim_object(slim::body_entries(system, body))])
}

/// `AddBalls2` for a mineable asteroid rock.
pub fn add_asteroid(stamp: u32, asteroid: &crate::wreck::Asteroid, now: i64) -> Result<Value, BallError> {
    let buffer = state_buffer(ADD_BALLS, stamp, &[asteroid.ball()])?;
    let slim = Value::Dict(asteroid.slim_entries().into_iter().map(|(k, v)| (word(k), v)).collect());
    Ok(action(stamp, "AddBalls2", [tuple([buffer, list([tuple([slim, wreck_damage_state(now)])])])]))
}

/// A depleted asteroid dissolves before the client removes its ball.
pub fn dissolve(stamp: u32, asteroid_id: i64) -> Vec<Value> {
    vec![
        action(stamp, "TerminalPlayDestructionEffect", [id(asteroid_id), int(crate::destruction::DISSOLVE)]),
        remove_ships(stamp, [asteroid_id]),
    ]
}

/// `OnSlimItemChange`: the slim item of a wreck or a can is told again (what is in it, who may take it), as an object.
pub fn slim_item_change(stamp: u32, wreck: &Wreck) -> Value {
    action(stamp, "OnSlimItemChange", [id(wreck.id), slim_object(wreck.slim_entries())])
}

/// `OnSlimItemChange` for an asteroid: stock is distinct from wreck loot flags.
pub fn slim_asteroid_change(stamp: u32, asteroid: &crate::wreck::Asteroid) -> Value {
    action(stamp, "OnSlimItemChange", [id(asteroid.id), slim_object(asteroid.slim_entries())])
}

/// `OnSlimItemChange` for a ship: its slim item is told again, as an object, to whoever has the ship in view (a pilot whose security status changed, for one).
pub fn ship_slim_item_change(stamp: u32, ship: &Ship) -> Value {
    action(stamp, "OnSlimItemChange", [id(ship.spec.ship_id), slim_object(slim::ship_entries(ship))])
}

/// `OnSlimItemChange` for a ship with `extra` entries added to its slim item (the damage over time it takes, `dotHpPercentageApplied`).
pub fn ship_slim_item_change_with(stamp: u32, ship: &Ship, extra: Vec<(&'static str, Value)>) -> Value {
    let mut entries = slim::ship_entries(ship);
    entries.extend(extra);
    action(stamp, "OnSlimItemChange", [id(ship.spec.ship_id), slim_object(entries)])
}

/// The effect the client plays where a pilot jettisons cargo (`effects.Jettison`, played on the can: it has no module, target or charge).
pub fn jettison_effect(stamp: u32, can_id: i64) -> Value {
    action(
        stamp,
        "OnSpecialFX",
        [
            id(can_id),
            Value::None,
            Value::None,
            Value::None,
            Value::None,
            text("effects.Jettison"),
            int(0),
            int(1),
            int(0),
            int(-1),
            Value::None,
            Value::None,
            int(0),
            Value::None,
        ],
    )
}

/// A ship is destroyed: the client plays the explosion where it was, and the ball goes (`TerminalPlayDestructionEffect`, `RemoveBalls`, one stamp).
pub fn destruction(stamp: u32, ship_id: i64) -> Vec<Value> {
    vec![
        action(stamp, "TerminalPlayDestructionEffect", [id(ship_id), int(DESTRUCTION_EFFECT_EXPLOSION)]),
        remove_ships(stamp, [ship_id]),
    ]
}

/// `RemoveBalls`: the balls of these ships are gone.
pub fn remove_ships(stamp: u32, ship_ids: impl IntoIterator<Item = i64>) -> Value {
    action(stamp, "RemoveBalls", [list(ship_ids.into_iter().map(id))])
}

/// The same removal as the reference sends it while a ship is warping away or in: inside a `PackagedAction`, the action marshalled whole.
pub fn remove_ships_packaged(stamp: u32, ship_ids: impl IntoIterator<Item = i64>) -> Value {
    let inner =
        eve_marshal::encode(&tuple([remove_ships(stamp, ship_ids)])).expect("the sim's own values encode");
    tuple([id(i64::from(stamp)), tuple([word("PackagedAction"), Value::Buffer(inner.into())])])
}

/// The ball of a body of the system: a structure is a station's ball (rigid, global, dockable).
pub(crate) fn rigid_ball(system: &SolarSystem, index: usize) -> BallEntity {
    let body = &system.bodies[index];
    BallEntity {
        item_id: eve_destiny::IdRef::Valid(body.id),
        kind: match body.kind {
            BodyKind::Station | BodyKind::Structure => eve_destiny::Kind::Station,
            BodyKind::Stargate => eve_destiny::Kind::Stargate,
            _ => eve_destiny::Kind::Other,
        },
        radius: body.radius,
        position: body.position,
        ..BallEntity::default()
    }
}

/// The empty table of drones (`droneState`).
fn drone_state() -> Value {
    let columns =
        ["droneID", "ownerID", "controllerID", "activityState", "typeID", "controllerOwnerID", "targetID"];
    slim::rowset(&columns, [])
}

/// `SetState`: every ball of the system and the ships in view of `ego` (itself included, in the order they came into the scene), with slim
/// items, damage and the system's own item.
pub fn set_state(
    stamp: u32,
    system: &SolarSystem,
    ego: &Ship,
    ships: &[&Ship],
    wrecks: &[&Wreck],
    now: i64,
) -> Result<Value, BallError> {
    set_state_for_ego(stamp, system, ego.spec.ship_id, ships, wrecks, &std::collections::BTreeMap::new(), now)
}

/// Builds a ballpark state with an authoritative ego item id that is not a ship.
pub(crate) fn set_state_for_ego(
    stamp: u32,
    system: &SolarSystem,
    ego_id: i64,
    ships: &[&Ship],
    wrecks: &[&Wreck],
    structure_health: &std::collections::BTreeMap<i64, crate::health::StructureHealth>,
    now: i64,
) -> Result<Value, BallError> {
    let mut balls: Vec<BallEntity> = (0..system.bodies.len()).map(|i| rigid_ball(system, i)).collect();
    balls.extend(ships.iter().map(|ship| ship.ball()));
    balls.extend(wrecks.iter().map(|wreck| wreck.ball()));

    let mut damage: Vec<(Value, Value)> = system
        .bodies
        .iter()
        .filter(|b| matches!(b.kind, BodyKind::Station | BodyKind::Structure))
        .map(|b| {
            let state = structure_health
                .get(&b.id)
                .map(|health| health.state_at(now))
                .unwrap_or_else(|| DamageState::whole(STATION_SHIELD_TAU_MS));
            (id(b.id), damage_state(&state, now))
        })
        .collect();
    damage.extend(
        ships.iter().map(|ship| (id(ship.spec.ship_id), damage_state(&ship.health.state_at(now), now))),
    );
    damage.extend(wrecks.iter().map(|wreck| (id(wreck.id), wreck_damage_state(now))));

    let mut slims: Vec<Value> =
        system.bodies.iter().map(|b| slim_object(slim::body_entries(system, b))).collect();
    slims.extend(ships.iter().map(|ship| slim_object(slim::ship_entries(ship))));
    slims.extend(wrecks.iter().map(|wreck| slim_object(wreck.slim_entries())));

    let state = key_val([
        ("stamp", id(i64::from(stamp))),
        ("state", state_buffer(SET_STATE, stamp, &balls)?),
        ("ego", id(ego_id)),
        ("industryLevel", int(0)),
        ("researchLevel", int(0)),
        ("damageState", Value::Dict(damage)),
        ("dbuffState", list([])),
        ("aggressors", Value::Dict(Vec::new())),
        ("droneState", drone_state()),
        ("slims", list(slims)),
        ("solItem", slim::system_row(system)),
        ("effectStates", list([])),
        ("allianceBridges", list([])),
    ]);

    Ok(action(stamp, "SetState", [state]))
}

/// `OnSpecialFX` announcing the ship (the uncloak effect the client plays when it appears in space).
pub fn uncloak(stamp: u32, ship_id: i64) -> Value {
    action(
        stamp,
        "OnSpecialFX",
        [
            id(ship_id),
            Value::None,
            Value::None,
            Value::None,
            Value::None,
            text("effects.Uncloak"),
            int(0),
            int(1),
            int(0),
            int(6000),
            Value::None,
            Value::None,
            int(0),
            Value::None,
        ],
    )
}
