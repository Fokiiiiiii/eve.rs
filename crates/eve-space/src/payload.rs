//! The destiny stream's payloads: the state buffer, `AddBalls2`, `SetState`, and the actions a command is answered with.

use eve_destiny::{BallEntity, BallError, encode_entity_ball};
use eve_marshal::Value;

use crate::ship::Ship;
use crate::slim;
use crate::wire::{filetime, id, int, key_val, list, real, slim_object, text, tuple, word};
use crate::world::{BodyKind, SolarSystem};

/// The kind byte of a state buffer: 0 for `SetState`, 1 for `AddBalls2`.
const SET_STATE: u8 = 0;
const ADD_BALLS: u8 = 1;

/// The shield fraction, its recharge and armor and structure fractions of something that can be hurt: `((shield, tau, time), armor, structure)`.
pub fn damage_state(shield_tau_ms: f64, now: i64) -> Value {
    tuple([tuple([real(1.0), real(shield_tau_ms), filetime(now)]), real(1.0), real(1.0)])
}

/// A station always answers with the same recharge, whatever it is.
const STATION_SHIELD_TAU_MS: f64 = 110_000.0;

fn state_buffer(kind: u8, stamp: u32, balls: &[BallEntity]) -> Result<Value, BallError> {
    let mut bytes = vec![kind];
    bytes.extend_from_slice(&stamp.to_le_bytes());
    for ball in balls {
        bytes.extend_from_slice(&encode_entity_ball(ball)?);
    }
    Ok(Value::Buffer(bytes.into()))
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
    Value::Dict(slim::ship_entries(&ship.spec).into_iter().map(|(k, v)| (word(k), v)).collect())
}

/// `AddBalls2` for the ship alone: the buffer with its ball, and the slim item with the damage state.
pub fn add_ship(stamp: u32, ship: &Ship, now: i64) -> Result<Value, BallError> {
    let buffer = state_buffer(ADD_BALLS, stamp, &[ship.ball()])?;
    let extra = tuple([ship_dict(ship), damage_state(ship.spec.shield_recharge_ms / 5.0, now)]);
    Ok(action(stamp, "AddBalls2", [tuple([buffer, list([extra])])]))
}

fn rigid_ball(system: &SolarSystem, index: usize) -> BallEntity {
    let body = &system.bodies[index];
    BallEntity {
        item_id: eve_destiny::IdRef::Valid(body.id),
        kind: match body.kind {
            BodyKind::Station => eve_destiny::Kind::Station,
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

/// `SetState`: every ball of the system and the ship, with slim items, damage and the system's own item.
pub fn set_state(stamp: u32, system: &SolarSystem, ship: &Ship, now: i64) -> Result<Value, BallError> {
    let mut balls: Vec<BallEntity> = (0..system.bodies.len()).map(|i| rigid_ball(system, i)).collect();
    balls.push(ship.ball());

    let mut damage: Vec<(Value, Value)> = system
        .bodies
        .iter()
        .filter(|b| b.kind == BodyKind::Station)
        .map(|b| (id(b.id), damage_state(STATION_SHIELD_TAU_MS, now)))
        .collect();
    damage.push((id(ship.spec.ship_id), damage_state(ship.spec.shield_recharge_ms / 5.0, now)));

    let mut slims: Vec<Value> =
        system.bodies.iter().map(|b| slim_object(slim::body_entries(system, b))).collect();
    slims.push(slim_object(slim::ship_entries(&ship.spec)));

    let state = key_val([
        ("stamp", id(i64::from(stamp))),
        ("state", state_buffer(SET_STATE, stamp, &balls)?),
        ("ego", id(ship.spec.ship_id)),
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
