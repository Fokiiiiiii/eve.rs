//! `behaviors/actions/ballparks.py`, the part that moves a ball: warping, going to and approaching a point or an object, orbiting, aligning, the
//! micro warp drive, and the tasks that put a ball somewhere.
//!
//! What a ball tells when it is in range (`callback=(self.OnApproachNotify, ())`) is a call of the task the world makes with `entered`; the loop that
//! manages the micro warp drive is a call of the task that asks for itself again after the time it sleeps.

use crate::blackboards::{Address, ScopeType};
use crate::constants::combat::{
    COMBAT_ORBIT_COMMANDER_VELOCITY_FACTOR, COMBAT_ORBIT_MWD_DISTANCE_FACTOR_LONG_DISTANCE,
    COMBAT_ORBIT_MWD_DISTANCE_FACTOR_SHORT_DISTANCE, COMBAT_ORBIT_MWD_DISTANCE_MAXIMUM,
    COMBAT_ORBIT_MWD_DISTANCE_MINIMUM, COMBAT_ORBIT_MWD_DISTANCE_THRESHOLD,
};
use crate::consts::*;
use crate::env::{NpcWarpKind, WorldError};
use crate::evemath::{
    random_vector, vec_add, vec_direction, vec_dot, vec_length, vec_normalize, vec_scale, vec_sub,
};
use crate::events::message::ON_BALL_MODE_CHANGED;
use crate::groups::mixin::get_member_id_list;
use crate::tree::{BehaviorError, Task, TaskCtx, blocking};
use crate::utility::ballparks::{get_ball, get_ball_position, get_my_ball, get_my_position, is_ball_in_park};
use crate::utility::dogmatic::{get_my_attribute_value, get_type_attribute_value};
use crate::utility::roles::{has_combat_role, is_commander};
use crate::value::Value;

/// `ALIGN_START_TIME`.
fn align_start_time() -> Address {
    Address::new(ScopeType::Item, "ALIGN_START_TIME")
}

fn type_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("TypeError: {what}")));
}

fn attribute_error(t: &mut TaskCtx<'_, '_>, what: &str) {
    t.raise(BehaviorError::Task(format!("AttributeError: 'NoneType' object has no attribute '{what}'")));
}

/// A position that a blackboard holds, as `*position` and the vector maths take it.
fn position_of(t: &mut TaskCtx<'_, '_>, value: &Value) -> Option<[f64; 3]> {
    let position = value.as_vec3();
    if position.is_none() {
        type_error(t, "a position of three numbers is required");
    }
    position
}

/// A number that a task attribute holds (a float that is not there raises where the client uses it).
fn number_of(t: &mut TaskCtx<'_, '_>, value: &Value) -> Option<f64> {
    let number = value.as_float();
    if number.is_none() {
        type_error(t, "a number is required");
    }
    number
}

/// `getattr(self.attributes, name, default)` of a number.
fn attribute_or(t: &mut TaskCtx<'_, '_>, name: &str, default: f64) -> Option<f64> {
    if t.has_attribute(name) {
        let value = t.attribute(name);
        number_of(t, &value)
    } else {
        Some(default)
    }
}

// ---- WarpToNewLocation, WarpToItem ----

const HANDLER_DESTINY_MODE_CHANGE: u16 = 0;

/// Warps to the location on the blackboard, and blocks the tree from resetting until the ship has left warp. `WarpToItem` warps to an item.
#[derive(Debug, Default)]
pub struct WarpToNewLocation {
    to_item: bool,
}

impl WarpToNewLocation {
    pub fn to_location() -> Self {
        WarpToNewLocation { to_item: false }
    }

    pub fn to_item() -> Self {
        WarpToNewLocation { to_item: true }
    }

    /// `GetDestinationCoordinate`.
    fn get_destination_coordinate(&self, t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if !self.to_item {
            return t.value_at("locationMessage");
        }
        let item = t.value_at("warpToItemIdAddress")?;
        if item.is_none() {
            return Some(Value::None);
        }
        let item_id = t.expect_id(&item)?;
        if !is_ball_in_park(t, item_id) {
            return Some(Value::None);
        }
        let me = t.my_item_id()?;
        Some(Value::from_vec3(t.world().warp_destination_point(me, item_id)))
    }

    /// `GetJitterRadius`.
    fn get_jitter_radius(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
        let jitter = t.attribute("jitterRadius");
        if t.has_attribute("jitterRadius") && !jitter.is_none() {
            number_of(t, &jitter)
        } else {
            Some(20000.0)
        }
    }

    /// `GetWarpinDistance`.
    fn get_warpin_distance(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
        if t.has_attribute("warpAtDistanceAddress") {
            let value = t.value_at("warpAtDistanceAddress")?;
            if !value.is_none() {
                return number_of(t, &value);
            }
        }
        Some(0.0)
    }

    /// `GetWarpSpeed`: 6 unless the ship has a warp speed multiplier that means it.
    fn get_warp_speed(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
        let warp_speed = get_my_attribute_value(t, ATTRIBUTE_WARP_SPEED_MULTIPLIER)?;
        Some(if warp_speed == 0.0 || warp_speed < 0.01 { 6.0 } else { warp_speed })
    }

    /// `myBall.NPCEndWarp()`.
    fn end_warp(t: &mut TaskCtx<'_, '_>, me: i64) -> bool {
        let result = t.world().npc_end_warp(me);
        t.ok(result).is_some()
    }
}

impl Task for WarpToNewLocation {
    fn type_name(&self) -> &'static str {
        if self.to_item { "WarpToItem" } else { "WarpToNewLocation" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        blocking::on_enter(t);
        t.set_status_to_failed();
        let Some(coordinate) = self.get_destination_coordinate(t) else { return };
        let Some(jitter_radius) = Self::get_jitter_radius(t) else { return };
        let Some(warp_at_range) = Self::get_warpin_distance(t) else { return };
        let Some(warp_speed) = Self::get_warp_speed(t) else { return };
        if !coordinate.truthy() {
            return;
        }
        let Some(destination) = position_of(t, &coordinate) else { return };
        let Some(me) = t.my_item_id() else { return };
        let Some(warped) = t.world().npc_warp_to(me, destination, jitter_radius, warp_at_range, warp_speed)
        else {
            attribute_error(t, "NPCWarpTo");
            return;
        };
        match warped {
            Ok(()) => {
                t.subscribe_item(me, ON_BALL_MODE_CHANGED, HANDLER_DESTINY_MODE_CHANGE);
                let Some(address) = t.required_address("warpScrambledAddress") else { return };
                t.send_blackboard_value(&address, Value::Bool(false));
                t.set_status_to_blocked();
            }
            Err(WorldError::NpcWarp(NpcWarpKind::Bubbled | NpcWarpKind::Scrambled)) => {
                let Some(address) = t.required_address("warpScrambledAddress") else { return };
                t.send_blackboard_value(&address, Value::Bool(true));
                Self::end_warp(t, me);
            }
            Err(WorldError::NpcWarp(NpcWarpKind::PathBubbled)) => {
                Self::end_warp(t, me);
            }
            Err(WorldError::NpcWarp(NpcWarpKind::Other(_))) => {
                // a warp that cannot be ended is one of a ship that is gone
                match t.world().npc_end_warp(me) {
                    Ok(()) => {}
                    Err(WorldError::User(_) | WorldError::NpcWarp(_)) => {
                        t.raise(BehaviorError::Unrecoverable(
                            "Unable to end after a failed warp.  Entity is probably dead.".to_string(),
                        ))
                    }
                    Err(other) => t.raise(other.into()),
                }
            }
            Err(other) => t.raise(other.into()),
        }
    }

    fn on_exit(&mut self, t: &mut TaskCtx<'_, '_>) {
        blocking::on_exit(t);
    }

    /// `DestinyModeChange(oldMode, newMode)`: the warp is over when the ship leaves warp.
    fn on_event(&mut self, t: &mut TaskCtx<'_, '_>, _handler: u16, args: &[Value]) {
        if args.first().and_then(Value::as_int) == Some(DESTINY_MODE_WARP)
            && let Some(me) = t.my_item_id()
        {
            t.unsubscribe_item(me, ON_BALL_MODE_CHANGED, HANDLER_DESTINY_MODE_CHANGE);
            t.set_status_to_success();
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            if let Some(me) = t.my_item_id() {
                t.unsubscribe_item(me, ON_BALL_MODE_CHANGED, HANDLER_DESTINY_MODE_CHANGE);
            }
            t.set_status_to_invalid();
        }
    }
}

// ---- SelectCoordinateNear... ----

/// `mathCommon.RandomVector(maxDistance, maxDistance - minDistance)` of the task's attributes.
fn random_offset(t: &mut TaskCtx<'_, '_>) -> Option<[f64; 3]> {
    let max_distance = t.required_float("maxDistance")?;
    let min_distance = t.required_float("minDistance")?;
    Some(random_vector(max_distance, max_distance - min_distance, t.world()))
}

/// A coordinate somewhere within a distance of a location (an item of the universe, or where I am).
#[derive(Debug, Default)]
pub struct SelectCoordinateNearLocationAction;

impl Task for SelectCoordinateNearLocationAction {
    fn type_name(&self) -> &'static str {
        "SelectCoordinateNearLocationAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(location) = t.value_at("locationAddress") else { return };
        // `GetReferenceCoordinates`
        let reference = if location.is_none() {
            let Some(ball) = get_my_ball(t) else { return };
            ball.position
        } else {
            let Some(location_id) = t.expect_id(&location) else { return };
            match t.world().location_position(location_id) {
                Some(position) => position,
                None => {
                    t.raise(WorldError::Key(location_id.to_string()).into());
                    return;
                }
            }
        };
        let Some(offset) = random_offset(t) else { return };
        let Some(address) = t.required_address("coordinateAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(vec_add(reference, offset)));
        t.set_status_to_success();
    }
}

/// A coordinate somewhere within a distance of a coordinate (`SelectCoordinateNearBall`: of a ball).
#[derive(Debug, Default)]
pub struct SelectCoordinateNearCoordinateAction {
    near_ball: bool,
}

impl SelectCoordinateNearCoordinateAction {
    pub fn near_coordinate() -> Self {
        SelectCoordinateNearCoordinateAction { near_ball: false }
    }

    pub fn near_ball() -> Self {
        SelectCoordinateNearCoordinateAction { near_ball: true }
    }

    /// `_GetSourceCoordinates`.
    fn get_source_coordinates(&self, t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if !self.near_ball {
            return t.value_at("sourceCoordinateAddress");
        }
        let ball = t.value_at("targetBallAddress")?;
        let ball_id = t.expect_id(&ball)?;
        let ball = get_ball(t, ball_id)?;
        Some(Value::from_vec3(ball.position))
    }
}

impl Task for SelectCoordinateNearCoordinateAction {
    fn type_name(&self) -> &'static str {
        if self.near_ball { "SelectCoordinateNearBall" } else { "SelectCoordinateNearCoordinateAction" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(source) = self.get_source_coordinates(t) else { return };
        let source_point = if source.truthy() {
            let Some(point) = position_of(t, &source) else { return };
            point
        } else {
            let Some(point) = get_my_position(t) else { return };
            point
        };
        let Some(offset) = random_offset(t) else { return };
        let Some(address) = t.required_address("destinationCoordinateAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(vec_add(source_point, offset)));
        t.set_status_to_success();
    }
}

// ---- ApproachCoordinatesAction, ApproachObject ----

const HANDLER_APPROACH_NOTIFY: u16 = 0;

/// `OnApproachNotify` and its like: the ball is in range; the movement is over and so is the task.
fn on_notified(t: &mut TaskCtx<'_, '_>) {
    if let Some(me) = t.my_item_id() {
        t.world().stop_movement_events(me);
    }
    if !t.is_invalid() {
        t.set_status_to_success();
        t.on_status_updated();
    }
}

/// Goes to a coordinate; when it blocks, until it is within the notify range.
#[derive(Debug, Default)]
pub struct ApproachCoordinatesAction;

impl Task for ApproachCoordinatesAction {
    fn type_name(&self) -> &'static str {
        "ApproachCoordinatesAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(coordinate) = t.value_at("coordinateAddress") else { return };
        if coordinate.is_none() {
            t.set_status_to_failed();
            return;
        }
        let Some(blocking) = t.required("blocking") else { return };
        let Some(position) = position_of(t, &coordinate) else { return };
        let Some(me) = t.my_item_id() else { return };
        if blocking.truthy() {
            let Some(notify_range) = t.required_float("notifyRange") else { return };
            let observer = t.observer(HANDLER_APPROACH_NOTIFY);
            t.world().goto_point_notify(me, position, notify_range, observer);
            t.set_status_to_suspended();
        } else {
            let result = t.world().goto_point(me, position);
            if t.ok(result).is_none() {
                return;
            }
            t.set_status_to_success();
        }
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, _args: &[Value]) {
        if handler == HANDLER_APPROACH_NOTIFY {
            on_notified(t);
        }
    }
}

/// Approaches an item; when it blocks, until it is within the notify range or the range to approach to.
#[derive(Debug, Default)]
pub struct ApproachObject;

impl ApproachObject {
    /// `_GetApproachRange`.
    fn get_approach_range(t: &mut TaskCtx<'_, '_>) -> Option<Value> {
        if t.has_attribute("approachRangeAddress") && t.attribute("approachRangeAddress").truthy() {
            t.value_at("approachRangeAddress")
        } else {
            t.required("approachRange")
        }
    }
}

impl Task for ApproachObject {
    fn type_name(&self) -> &'static str {
        "ApproachObject"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(item) = t.value_at("itemIdAddress") else { return };
        let Some(item_id) = item.as_int() else { return };
        if !t.world().has_ball(item_id) {
            return;
        }
        let Some(approach_range) = Self::get_approach_range(t) else { return };
        let Some(approach_range) = number_of(t, &approach_range) else { return };
        let Some(blocking) = t.required("blocking") else { return };
        let Some(me) = t.my_item_id() else { return };
        if blocking.truthy() {
            let Some(notify_range) = t.required_float("notifyRange") else { return };
            let observer = t.observer(HANDLER_APPROACH_NOTIFY);
            t.world().approach_object_notify(
                me,
                item_id,
                notify_range.max(approach_range),
                approach_range,
                observer,
            );
            t.set_status_to_suspended();
        } else {
            t.world().approach_object(me, item_id, approach_range, false);
            t.set_status_to_success();
        }
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, _args: &[Value]) {
        if handler == HANDLER_APPROACH_NOTIFY {
            on_notified(t);
        }
    }
}

// ---- AlignToCoordinatesAction ----

/// Turns the ship towards a coordinate (at least 30 km away) until it flies in that direction, or the time is out.
#[derive(Debug, Default)]
pub struct AlignToCoordinatesAction;

impl AlignToCoordinatesAction {
    /// `_MakeSureTargetPointIsFarAway`.
    fn make_sure_target_point_is_far_away(t: &mut TaskCtx<'_, '_>) -> Option<()> {
        const MIN_DIST: f64 = 30000.0;
        let coordinate = t.value_at("coordinateAddress")?;
        let coordinate = position_of(t, &coordinate)?;
        let my_position = get_my_position(t)?;
        let direction = vec_sub(coordinate, my_position);
        if direction.iter().map(|c| c * c).sum::<f64>() < MIN_DIST * MIN_DIST {
            let far = vec_add(my_position, vec_scale(vec_normalize(direction), MIN_DIST));
            let address = t.required_address("coordinateAddress")?;
            t.send_blackboard_value(&address, Value::from_vec3(far));
        }
        Some(())
    }

    /// `_HasTimedOut`.
    fn has_timed_out(t: &mut TaskCtx<'_, '_>) -> Option<bool> {
        let time_out_sec = attribute_or(t, "timeoutSeconds", 60.0)?;
        let start = t.get_last_blackboard_value(&align_start_time());
        let Some(start) = start.as_int() else {
            type_error(t, "unsupported operand type(s) for -: 'int' and 'NoneType'");
            return None;
        };
        Some((t.now() - start) as f64 > time_out_sec * SEC as f64)
    }

    /// `_HasSuccessfullyAligned`.
    fn has_successfully_aligned(t: &mut TaskCtx<'_, '_>, align_direction: [f64; 3]) -> Option<bool> {
        let acceptable_deviation = attribute_or(t, "acceptableAngleDeviationDegrees", 1.0)?;
        let ball = get_my_ball(t)?;
        let fighter_direction = vec_normalize(ball.velocity);
        let dot_product = vec_dot(align_direction, fighter_direction).clamp(-1.0, 1.0);
        Some(dot_product.acos().to_degrees() < acceptable_deviation)
    }

    /// `_UpdateGotoPointAndReturnNormalizedDirection`: nothing for a coordinate that is not there.
    fn update_goto_point_and_return_normalized_direction(
        t: &mut TaskCtx<'_, '_>,
    ) -> Option<Option<[f64; 3]>> {
        let coordinate = t.value_at("coordinateAddress")?;
        if coordinate.is_none() {
            t.set_status_to_failed();
            return Some(None);
        }
        let coordinate = position_of(t, &coordinate)?;
        let my_position = get_my_position(t)?;
        let normalized_direction = vec_normalize(vec_sub(coordinate, my_position));
        let goto_point = vec_add(my_position, vec_scale(normalized_direction, 5000.0));
        let me = t.my_item_id()?;
        let result = t.world().goto_point(me, goto_point);
        t.ok(result)?;
        Some(Some(normalized_direction))
    }

    fn stop_movement(t: &mut TaskCtx<'_, '_>) {
        if let Some(me) = t.my_item_id() {
            t.world().stop_movement(me);
        }
    }
}

impl Task for AlignToCoordinatesAction {
    fn type_name(&self) -> &'static str {
        "AlignToCoordinatesAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let now = t.now();
        t.send_blackboard_value(&align_start_time(), Value::Int(now));
        if Self::make_sure_target_point_is_far_away(t).is_none() {
            return;
        }
        t.set_status_to_running();
    }

    fn update(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(direction) = Self::update_goto_point_and_return_normalized_direction(t) else { return };
        let Some(direction) = direction else {
            // the client goes on to compare the direction that is not there
            type_error(t, "an alignment needs a direction");
            return;
        };
        let Some(aligned) = Self::has_successfully_aligned(t, direction) else { return };
        if aligned {
            t.set_status_to_success();
            Self::stop_movement(t);
            return;
        }
        let Some(timed_out) = Self::has_timed_out(t) else { return };
        if timed_out {
            t.set_status_to_failed();
            Self::stop_movement(t);
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_running() {
            t.set_status_to_invalid();
        }
    }
}

// ---- OrbitAtDistanceAction ----

const HANDLER_IN_ORBIT_NOTIFY: u16 = 0;

/// Orbits an item at a distance; when it blocks, until it is within the range it says.
#[derive(Debug, Default)]
pub struct OrbitAtDistanceAction;

impl OrbitAtDistanceAction {
    /// `_GetOrbitRange`.
    fn get_orbit_range(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
        if t.has_attribute("orbitRangeAddress") && t.attribute("orbitRangeAddress").truthy() {
            let range = t.value_at("orbitRangeAddress")?;
            return number_of(t, &range);
        }
        let range = t.attribute("orbitRange");
        if t.has_attribute("orbitRange") && range.truthy() {
            return number_of(t, &range);
        }
        t.raise(BehaviorError::Authoring(
            "no orbit range defined for task: OrbitAtDistanceAction".to_string(),
        ));
        None
    }

    /// `OrbitBlocking` and `OrbitNonBlocking`.
    fn orbit(
        t: &mut TaskCtx<'_, '_>,
        me: i64,
        target_id: i64,
        orbit_range: f64,
    ) -> Option<Result<(), WorldError>> {
        let blocking = t.required("blocking")?;
        if blocking.truthy() {
            let block_until_range = if t.has_attribute("blockUntilRangeAddress")
                && t.attribute("blockUntilRangeAddress").truthy()
            {
                let range = t.value_at("blockUntilRangeAddress")?;
                number_of(t, &range)?
            } else {
                let range = t.required("blockUntilRange")?;
                if range.truthy() { number_of(t, &range)? } else { 1000.0 }
            };
            let observer = t.observer(HANDLER_IN_ORBIT_NOTIFY);
            let result =
                t.world().orbit_object(me, target_id, orbit_range, Some((block_until_range, observer)));
            if result.is_ok() {
                t.set_status_to_suspended();
            }
            Some(result)
        } else {
            let result = t.world().orbit_object(me, target_id, orbit_range, None);
            if result.is_ok() {
                t.set_status_to_success();
            }
            Some(result)
        }
    }
}

impl Task for OrbitAtDistanceAction {
    fn type_name(&self) -> &'static str {
        "OrbitAtDistanceAction"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(my_ball) = get_my_ball(t) else { return };
        if t.world().is_ball_released(my_ball.id) {
            t.raise(BehaviorError::Unrecoverable(String::new()));
            return;
        }
        let Some(target) = t.value_at("orbitTargetAddress") else { return };
        let Some(target_id) = target.as_int() else {
            t.set_status_to_failed();
            return;
        };
        if !t.world().has_ball(target_id) {
            t.set_status_to_failed();
            return;
        }
        let Some(orbit_range) = Self::get_orbit_range(t) else { return };
        let Some(orbited) = Self::orbit(t, my_ball.id, target_id, orbit_range) else { return };
        let mut outcome = orbited;
        if outcome.is_ok() && t.has_attribute("velocityFraction") {
            // `_set_velocity_fraction`
            let Some(fraction) = t.required_float("velocityFraction") else { return };
            outcome = t.world().set_speed_fraction(my_ball.id, fraction);
        }
        match outcome {
            Ok(()) => {}
            Err(WorldError::User(_)) => t.set_status_to_failed(),
            Err(other) => t.raise(other.into()),
        }
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, _args: &[Value]) {
        if handler == HANDLER_IN_ORBIT_NOTIFY {
            on_notified(t);
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && t.has_attribute("velocityFraction")
            && let Some(me) = t.my_item_id()
        {
            let result = t.world().set_speed_fraction(me, 1.0);
            t.ok(result);
        }
        t.set_status_to_invalid();
    }
}

// ---- FullStopAction, GetBallGotoPointAction, GotoPosition, ... ----

enter_task!(
    /// Stops the ball.
    FullStopAction,
    |t| {
        let Some(ball) = get_my_ball(t) else { return };
        t.world().stop_movement(ball.id);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts where a ball is going on the blackboard.
    GetBallGotoPointAction,
    |t| {
        let Some(ball) = t.value_at("ballIdAddress") else { return };
        let Some(ball_id) = t.expect_id(&ball) else { return };
        let Some(ball) = get_ball(t, ball_id) else { return };
        let Some(address) = t.required_address("gotoPointAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(ball.goto));
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts where I am on the blackboard.
    PostOwnPosition,
    |t| {
        let Some(ball) = get_my_ball(t) else { return };
        let Some(address) = t.required_address("coordinateAddress") else { return };
        t.send_blackboard_value(&address, Value::from_vec3(ball.position));
        t.set_status_to_success();
    }
);

enter_task!(
    /// Goes to the coordinates on the blackboard.
    GotoPosition,
    |t| {
        let Some(coordinate) = t.value_at("coordinateAddress") else { return };
        let Some(position) = position_of(t, &coordinate) else { return };
        let Some(me) = t.my_item_id() else { return };
        let result = t.world().goto_point(me, position);
        if t.ok(result).is_none() {
            return;
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts the ball at the location on the blackboard.
    TransferToLocation,
    |t| {
        t.set_status_to_failed();
        let Some(location) = t.value_at("locationAddress") else { return };
        if !location.truthy() {
            return;
        }
        let Some(position) = position_of(t, &location) else { return };
        let Some(ball) = get_my_ball(t) else { return };
        t.world().change_ball_position(ball.id, position);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Puts an item's ball at the location on the blackboard.
    TransferItemToLocation,
    |t| {
        t.set_status_to_failed();
        let Some(item) = t.value_at("itemIdAddress") else { return };
        let Some(location) = t.value_at("locationAddress") else { return };
        if !(location.truthy() && item.truthy()) {
            return;
        }
        let Some(position) = position_of(t, &location) else { return };
        let Some(item_id) = t.expect_id(&item) else { return };
        t.world().change_ball_position(item_id, position);
        t.set_status_to_success();
    }
);

/// The reason of a task that makes a ball invulnerable or not.
fn reason_of(t: &mut TaskCtx<'_, '_>) -> Option<String> {
    let reason = t.required("reason")?;
    Some(reason.as_str().map_or_else(|| reason.to_string(), str::to_string))
}

enter_task!(
    /// Makes me invulnerable for good.
    MakeSelfInvulnerable,
    |t| {
        let Some(ball) = get_my_ball(t) else { return };
        let Some(reason) = reason_of(t) else { return };
        t.world().make_invulnerable_permanently(ball.id, &reason);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Takes the invulnerability I have away.
    CancelInvulnerabilityForSelf,
    |t| {
        let Some(ball) = get_my_ball(t) else { return };
        let Some(reason) = reason_of(t) else { return };
        t.world().cancel_current_invulnerability(ball.id, &reason);
        t.set_status_to_success();
    }
);

enter_task!(
    /// Cloaks my ship.
    Cloak,
    |t| {
        if let Some(me) = t.my_item_id() {
            t.world().cloak_ship(me);
        }
        t.set_status_to_success();
    }
);

enter_task!(
    /// Uncloaks my ship.
    Uncloak,
    |t| {
        if let Some(me) = t.my_item_id() {
            t.world().uncloak_ship(me);
        }
        t.set_status_to_success();
    }
);

// ---- coordinates from directions and distances ----

enter_task!(
    /// The coordinates `distance` from me in a direction.
    SelectCoordinatesFromDirectionIntoDistance,
    |t| {
        t.set_status_to_success();
        let Some(direction) = t.value_at("directionAddress") else { return };
        let Some(direction) = position_of(t, &direction) else { return };
        let direction = vec_normalize(direction);
        let Some(me) = t.my_item_id() else { return };
        let Some(ball) = get_ball(t, me) else { return };
        let Some(distance) = t.required_float("distance") else { return };
        let offset = vec_scale(direction, distance);
        let Some(address) = t.required_address("coordinatesAddress") else { return };
        let coordinates = (0..3).map(|i| Value::Float(ball.position[i] + offset[i])).collect();
        t.send_blackboard_value(&address, Value::List(coordinates));
    }
);

enter_task!(
    /// The coordinates, moved by a distance towards me.
    GetCoordinatesReducedByDistance,
    |t| {
        t.set_status_to_failed();
        let Some(coordinates) = t.value_at("sourceCoordinateAddress") else { return };
        let Some(me) = t.my_item_id() else { return };
        let Some(my_position) = get_ball_position(t, me) else { return };
        let mut coordinates = coordinates;
        if t.has_attribute("distanceAddress") {
            let Some(distance) = t.value_at("distanceAddress") else { return };
            if distance.truthy() {
                let Some(source) = position_of(t, &coordinates) else { return };
                let Some(distance) = number_of(t, &distance) else { return };
                let direction = vec_direction(my_position, source);
                coordinates = Value::from_vec3(vec_add(source, vec_scale(direction, distance)));
            }
        }
        let Some(address) = t.required_address("destinationCoordinateAddress") else { return };
        t.send_blackboard_value(&address, coordinates);
        t.set_status_to_success();
    }
);

// ---- FlyIntoRandomDirection ----

/// Flies in a random direction (at a fraction of the speed) for as long as it runs.
#[derive(Debug, Default)]
pub struct FlyIntoRandomDirection;

impl Task for FlyIntoRandomDirection {
    fn type_name(&self) -> &'static str {
        "FlyIntoRandomDirection"
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        let Some(me) = t.my_item_id() else { return };
        let direction = random_vector(1.0, 0.0, t.world());
        let result = t.world().goto_direction(me, direction);
        if t.ok(result).is_none() {
            return;
        }
        let Some(fraction) = t.required_float("velocityFraction") else { return };
        let result = t.world().set_speed_fraction(me, fraction);
        if t.ok(result).is_none() {
            return;
        }
        t.set_status_to_suspended();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid()
            && let Some(me) = t.my_item_id()
        {
            let result = t.world().set_speed_fraction(me, 1.0);
            t.ok(result);
        }
        t.set_status_to_invalid();
    }
}

// ---- ManageMicroWarpDriveToTarget, ManageMicroWarpDriveToCoordinates ----

const HANDLER_MANAGE_MICRO_WARP_DRIVE: u16 = 0;

/// Turns the micro warp drive on and off as a ball chases a target (or a coordinate) that is far enough away; the loop that decides is a thread of its
/// own for as long as the task lives and the balls are there.
#[derive(Debug, Default)]
pub struct ManageMicroWarpDrive {
    to_coordinates: bool,
    thread_running: bool,
    is_active: bool,
    target_id: Value,
    coordinates: Value,
}

impl ManageMicroWarpDrive {
    pub fn to_target() -> Self {
        ManageMicroWarpDrive { to_coordinates: false, ..Default::default() }
    }

    pub fn to_coordinates() -> Self {
        ManageMicroWarpDrive { to_coordinates: true, ..Default::default() }
    }

    /// `_IsTargetInSameBubble`.
    fn is_target_in_same_bubble(&self, t: &mut TaskCtx<'_, '_>) -> Option<bool> {
        if self.to_coordinates {
            let ball = get_my_ball(t)?;
            let position = position_of(t, &self.coordinates.clone())?;
            return Some(ball.new_bubble_id == t.world().bubble_at_coordinates(position));
        }
        let me = t.my_item_id()?;
        let target_id = t.expect_id(&self.target_id.clone())?;
        Some(t.world().in_same_bubble(me, target_id))
    }

    /// `_AreBallsStillThere`.
    fn are_balls_still_there(&self, t: &mut TaskCtx<'_, '_>) -> Option<bool> {
        let me = t.my_item_id()?;
        if self.to_coordinates {
            return Some(t.world().has_ball(me));
        }
        let target_id = t.expect_id(&self.target_id.clone())?;
        if !t.world().has_ball(target_id) || !t.world().has_ball(me) {
            return Some(false);
        }
        let cloaked = t.world().is_cloaked(target_id);
        Some(!t.ok(cloaked)?)
    }

    /// `_GetMicroWarpDriveDistance`.
    fn get_micro_warp_drive_distance(t: &mut TaskCtx<'_, '_>) -> Option<f64> {
        if t.has_attribute("microwarpdriveRangeAddress") {
            let range = t.value_at("microwarpdriveRangeAddress")?;
            return number_of(t, &range);
        }
        let orbit_distance = t.value_at("orbitRangeAddress")?;
        let orbit_distance = number_of(t, &orbit_distance)?;
        Some(if orbit_distance < COMBAT_ORBIT_MWD_DISTANCE_THRESHOLD {
            (orbit_distance * COMBAT_ORBIT_MWD_DISTANCE_FACTOR_SHORT_DISTANCE)
                .max(COMBAT_ORBIT_MWD_DISTANCE_MINIMUM)
        } else {
            (orbit_distance * COMBAT_ORBIT_MWD_DISTANCE_FACTOR_LONG_DISTANCE)
                .min(COMBAT_ORBIT_MWD_DISTANCE_MAXIMUM)
        })
    }

    /// `_IsOutsideMicroWarpDriveDistance`.
    fn is_outside_micro_warp_drive_distance(&self, t: &mut TaskCtx<'_, '_>) -> Option<bool> {
        let distance = if self.to_coordinates {
            let ball = get_my_ball(t)?;
            let coordinates = position_of(t, &self.coordinates.clone())?;
            vec_length(vec_sub(ball.position, coordinates))
        } else {
            let me = t.my_item_id()?;
            let target_id = t.expect_id(&self.target_id.clone())?;
            let distance = t.world().distance_between(me, target_id);
            t.ok(distance)?
        };
        Some(distance >= Self::get_micro_warp_drive_distance(t)?)
    }

    /// `_ShouldMicroWarpDrive`.
    fn should_micro_warp_drive(&self, t: &mut TaskCtx<'_, '_>) -> Option<bool> {
        if !self.is_outside_micro_warp_drive_distance(t)? {
            return Some(false);
        }
        let chance = t.required_float("microWarpDriveChance")?;
        Some(t.world().random() < chance)
    }

    /// `_SetMovementSpeed`.
    fn set_movement_speed(&mut self, t: &mut TaskCtx<'_, '_>, should_micro_warp_drive: bool) {
        if let Some(me) = t.my_item_id() {
            t.world().set_movement_speed(me, !should_micro_warp_drive);
        }
        self.is_active = should_micro_warp_drive;
    }

    /// One turn of `_ManageMicroWarpDrive`; `None` is a turn that raised.
    fn manage(&mut self, t: &mut TaskCtx<'_, '_>) -> Option<()> {
        if !t.is_invalid() && self.are_balls_still_there(t)? {
            let should = self.should_micro_warp_drive(t)?;
            if self.is_active != should {
                self.set_movement_speed(t, should);
            }
            // `SleepSim(microWarpDriveDuration / 1000)`: the client divides whole milliseconds by a whole number
            let duration = t.required("microWarpDriveDuration")?;
            let seconds = match duration {
                Value::Int(milliseconds) => milliseconds.div_euclid(1000) as f64,
                other => number_of(t, &other)? / 1000.0,
            };
            t.schedule_call(seconds, HANDLER_MANAGE_MICRO_WARP_DRIVE, Vec::new());
        } else {
            self.thread_running = false;
        }
        Some(())
    }
}

impl Task for ManageMicroWarpDrive {
    fn type_name(&self) -> &'static str {
        if self.to_coordinates { "ManageMicroWarpDriveToCoordinates" } else { "ManageMicroWarpDriveToTarget" }
    }

    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        if self.to_coordinates {
            let Some(coordinates) = t.value_at("coordinateAddress") else { return };
            self.coordinates = coordinates;
            if self.coordinates.is_none() {
                return;
            }
        } else {
            let Some(target) = t.value_at("targetAddress") else { return };
            self.target_id = target;
            if self.target_id.is_none() {
                return;
            }
        }
        match self.is_target_in_same_bubble(t) {
            Some(true) => {}
            _ => return,
        }
        if !self.thread_running {
            self.thread_running = true;
            t.schedule_call(0.0, HANDLER_MANAGE_MICRO_WARP_DRIVE, Vec::new());
        }
        t.set_status_to_success();
    }

    fn on_call(&mut self, t: &mut TaskCtx<'_, '_>, handler: u16, _args: &[Value]) {
        if handler == HANDLER_MANAGE_MICRO_WARP_DRIVE {
            // a turn that raises ends the thread, and the client's thread is not started again
            self.manage(t);
        }
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            self.is_active = false;
            self.set_movement_speed(t, false);
            t.set_status_to_invalid();
        }
    }
}

// ---- ModifyOrbitVelocity ----

/// Sets the velocity I orbit at: the group's commander at a fraction of the slowest of its fighters.
#[derive(Debug, Default)]
pub struct ModifyOrbitVelocity {
    orbit_velocity: Value,
}

impl ModifyOrbitVelocity {
    /// `_GetOrbitVelocityForType`.
    fn get_orbit_velocity_for_type(t: &mut TaskCtx<'_, '_>, type_id: i64) -> f64 {
        let cruise_speed = get_type_attribute_value(t, type_id, ATTRIBUTE_ENTITY_CRUISE_SPEED);
        let max_velocity = get_type_attribute_value(t, type_id, ATTRIBUTE_MAX_VELOCITY);
        if cruise_speed != 0.0 { cruise_speed } else { max_velocity }
    }

    /// `_GetSlowestOrbitVelocityOfGroup`.
    fn get_slowest_orbit_velocity_of_group(t: &mut TaskCtx<'_, '_>) -> Option<Option<f64>> {
        let mut slowest: Option<f64> = None;
        for entity_id in get_member_id_list(t) {
            let Some(item) = t.world().inventory_item(entity_id) else {
                t.raise(WorldError::Runtime(format!("no item {entity_id}")).into());
                return None;
            };
            if !has_combat_role(t, item.item_id) {
                continue;
            }
            let velocity = Self::get_orbit_velocity_for_type(t, item.type_id);
            if velocity != 0.0 && slowest.is_none_or(|slowest| velocity < slowest) {
                slowest = Some(velocity);
            }
        }
        if t.faulted() { None } else { Some(slowest) }
    }

    /// `_GetCustomOrbitVelocity`.
    fn get_custom_orbit_velocity(&self, t: &mut TaskCtx<'_, '_>, me: i64) -> Option<Value> {
        if is_commander(t, me) {
            let slowest = Self::get_slowest_orbit_velocity_of_group(t)?;
            return match slowest.filter(|&velocity| velocity != 0.0) {
                Some(velocity) => Some(Value::Float(velocity * COMBAT_ORBIT_COMMANDER_VELOCITY_FACTOR)),
                None => {
                    t.raise(BehaviorError::Authoring(
                        "Behavior received None or 0 as slowest combat velocity".to_string(),
                    ));
                    None
                }
            };
        }
        Some(self.orbit_velocity.clone())
    }
}

impl Task for ModifyOrbitVelocity {
    fn type_name(&self) -> &'static str {
        "ModifyOrbitVelocity"
    }

    /// The client sets the custom velocity when the velocity it worked out is nothing (`if not customVelocity`), and leaves the ball alone when there
    /// is one; it is ported as it is.
    fn on_enter(&mut self, t: &mut TaskCtx<'_, '_>) {
        t.set_status_to_failed();
        let Some(orbit_velocity) = t.required("orbitVelocity") else { return };
        self.orbit_velocity = orbit_velocity;
        let Some(me) = t.my_item_id() else { return };
        let Some(custom_velocity) = self.get_custom_orbit_velocity(t, me) else { return };
        if !custom_velocity.truthy() {
            let Some(ball) = get_my_ball(t) else { return };
            t.world().set_custom_orbit_velocity(ball.id, custom_velocity.as_float());
        }
        t.set_status_to_success();
    }

    fn clean_up(&mut self, t: &mut TaskCtx<'_, '_>) {
        if !t.is_invalid() {
            if !self.orbit_velocity.is_none()
                && let Some(ball) = get_my_ball(t)
            {
                t.world().remove_custom_orbit_velocity(ball.id);
            }
            t.set_status_to_invalid();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::env::testing::TestGroup;
    use crate::tree::tests::{Rig, with_task};
    use crate::tree::{Attrs, Status};

    fn item(name: &str) -> Value {
        Address::new(ScopeType::Item, name).to_value()
    }

    fn close(actual: &Value, expected: [f64; 3]) {
        let got = actual.as_vec3().unwrap_or_else(|| panic!("not a position: {actual:?}"));
        for i in 0..3 {
            assert!((got[i] - expected[i]).abs() < 1e-6, "{got:?} is not {expected:?}");
        }
    }

    /// I (1) am a ball at (100, 0, 0), and 2 is one at (300, 0, 0).
    fn base_rig() -> Rig {
        let mut rig = Rig::new();
        rig.world.add_ball(1, [100.0, 0.0, 0.0], 500);
        rig.world.add_ball(2, [300.0, 0.0, 0.0], 501);
        rig
    }

    // ---- WarpToNewLocation ----

    fn warp_attrs() -> Attrs {
        Attrs::new().with("locationMessage", item("Where")).with("warpScrambledAddress", item("Scrambled"))
    }

    fn warp_rig() -> Rig {
        let mut rig = base_rig();
        rig.world.npc_warpers.insert(1);
        rig.post(ScopeType::Item, "Where", Value::from_vec3([5.0, 6.0, 7.0]));
        rig
    }

    #[test]
    fn a_warp_starts_with_the_defaults_and_blocks_until_the_ship_leaves_warp() {
        let mut rig = warp_rig();
        assert_eq!(rig.run(Box::new(WarpToNewLocation::to_location()), warp_attrs()), Status::Blocking);
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.world.take_calls(), ["npc_warp_to(1, [5.0, 6.0, 7.0], 20000, 0, 6)"]);
        assert_eq!(rig.last(ScopeType::Item, "Scrambled"), Value::Bool(false));
        assert!(rig.tree.is_reset_blocked());
        // the ship that goes into warp is not what ends the task
        rig.send_event(1, ON_BALL_MODE_CHANGED, &[Value::Int(0), Value::Int(DESTINY_MODE_WARP)]);
        assert_eq!(rig.task_status(), Status::Blocking);
        rig.send_event(1, ON_BALL_MODE_CHANGED, &[Value::Int(DESTINY_MODE_WARP), Value::Int(0)]);
        assert_eq!(rig.task_status(), Status::Success);
    }

    #[test]
    fn the_jitter_the_distance_to_warp_at_and_the_warp_speed_are_what_the_task_and_the_ship_say() {
        let mut rig = warp_rig();
        rig.world.attributes.insert((1, ATTRIBUTE_WARP_SPEED_MULTIPLIER), 3.5);
        rig.post(ScopeType::Item, "Range", Value::Float(2500.0));
        let attrs = warp_attrs().with("jitterRadius", 300).with("warpAtDistanceAddress", item("Range"));
        rig.run(Box::new(WarpToNewLocation::to_location()), attrs);
        assert_eq!(rig.world.take_calls(), ["npc_warp_to(1, [5.0, 6.0, 7.0], 300, 2500, 3.5)"]);
        // no distance on the blackboard is no distance; a warp speed that is too little is the default; a jitter of nothing is the default
        let mut rig = warp_rig();
        rig.world.attributes.insert((1, ATTRIBUTE_WARP_SPEED_MULTIPLIER), 0.005);
        let attrs =
            warp_attrs().with("jitterRadius", Value::None).with("warpAtDistanceAddress", item("Range"));
        rig.run(Box::new(WarpToNewLocation::to_location()), attrs);
        assert_eq!(rig.world.take_calls(), ["npc_warp_to(1, [5.0, 6.0, 7.0], 20000, 0, 6)"]);
    }

    #[test]
    fn a_ship_that_cannot_warp_because_of_a_bubble_or_a_scrambler_says_so_and_ends_the_warp() {
        for kind in [NpcWarpKind::Bubbled, NpcWarpKind::Scrambled] {
            let mut rig = warp_rig();
            rig.world.npc_warp_error = Some(WorldError::NpcWarp(kind));
            assert_eq!(rig.run(Box::new(WarpToNewLocation::to_location()), warp_attrs()), Status::Failure);
            assert_eq!(rig.last_error, None);
            assert_eq!(rig.last(ScopeType::Item, "Scrambled"), Value::Bool(true));
            assert_eq!(rig.world.take_calls().last().unwrap(), "npc_end_warp(1)");
        }
        // a path that is bubbled is not one that says the ship is scrambled
        let mut rig = warp_rig();
        rig.post(ScopeType::Item, "Scrambled", Value::Bool(false));
        rig.world.npc_warp_error = Some(WorldError::NpcWarp(NpcWarpKind::PathBubbled));
        assert_eq!(rig.run(Box::new(WarpToNewLocation::to_location()), warp_attrs()), Status::Failure);
        assert_eq!(rig.last(ScopeType::Item, "Scrambled"), Value::Bool(false));
        assert_eq!(rig.world.take_calls().last().unwrap(), "npc_end_warp(1)");
    }

    #[test]
    fn a_warp_that_fails_otherwise_is_ended_and_one_that_cannot_be_ended_is_unrecoverable() {
        let mut rig = warp_rig();
        rig.world.npc_warp_error = Some(WorldError::NpcWarp(NpcWarpKind::Other("no".into())));
        assert_eq!(rig.run(Box::new(WarpToNewLocation::to_location()), warp_attrs()), Status::Failure);
        assert_eq!(rig.last_error, None);
        assert_eq!(rig.world.take_calls().last().unwrap(), "npc_end_warp(1)");
        for end_error in [WorldError::User("Dead".into()), WorldError::NpcWarp(NpcWarpKind::Scrambled)] {
            let mut rig = warp_rig();
            rig.world.npc_warp_error = Some(WorldError::NpcWarp(NpcWarpKind::Other("no".into())));
            rig.world.npc_end_warp_error = Some(end_error);
            rig.run(Box::new(WarpToNewLocation::to_location()), warp_attrs());
            assert!(matches!(rig.last_error, Some(BehaviorError::Unrecoverable(_))));
        }
        // an error that is not the warp's is not eaten
        let mut rig = warp_rig();
        rig.world.npc_warp_error = Some(WorldError::Runtime("boom".into()));
        rig.run(Box::new(WarpToNewLocation::to_location()), warp_attrs());
        assert_eq!(rig.last_error, Some(BehaviorError::World(WorldError::Runtime("boom".into()))));
    }

    #[test]
    fn no_location_is_a_failure_and_a_ball_that_cannot_warp_is_a_fault() {
        let mut rig = warp_rig();
        rig.post(ScopeType::Item, "Where", Value::None);
        assert_eq!(rig.run(Box::new(WarpToNewLocation::to_location()), warp_attrs()), Status::Failure);
        assert!(rig.world.take_calls().is_empty());
        let mut rig = warp_rig();
        rig.world.npc_warpers.clear();
        rig.run(Box::new(WarpToNewLocation::to_location()), warp_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.contains("NPCWarpTo")));
    }

    #[test]
    fn a_warp_to_an_item_goes_to_the_point_the_ballpark_gives_and_needs_the_item_in_the_park() {
        let attrs = Attrs::new()
            .with("warpToItemIdAddress", item("Item"))
            .with("warpScrambledAddress", item("Scrambled"));
        let mut rig = warp_rig();
        rig.post(ScopeType::Item, "Item", Value::Int(2));
        rig.world.warp_destination_points.insert((1, 2), [290.0, 0.0, 0.0]);
        assert_eq!(rig.run(Box::new(WarpToNewLocation::to_item()), attrs.clone()), Status::Blocking);
        assert_eq!(rig.world.take_calls(), ["npc_warp_to(1, [290.0, 0.0, 0.0], 20000, 0, 6)"]);
        assert_eq!(WarpToNewLocation::to_item().type_name(), "WarpToItem");
        for item_id in [Value::Int(77), Value::None] {
            let mut rig = warp_rig();
            rig.post(ScopeType::Item, "Item", item_id);
            assert_eq!(rig.run(Box::new(WarpToNewLocation::to_item()), attrs.clone()), Status::Failure);
            assert!(rig.world.take_calls().is_empty());
        }
    }

    #[test]
    fn the_warp_that_is_cleaned_up_no_longer_listens_to_the_ball() {
        let mut rig = warp_rig();
        rig.run(Box::new(WarpToNewLocation::to_location()), warp_attrs());
        assert_eq!(rig.shared.events.subscription_count(), 1);
        rig.clean_up_task();
        assert_eq!(rig.shared.events.subscription_count(), 0);
    }

    // ---- SelectCoordinateNear... ----

    fn near_attrs() -> Attrs {
        Attrs::new()
            .with("locationAddress", item("Location"))
            .with("coordinateAddress", item("Chosen"))
            .with("maxDistance", 10.0)
            .with("minDistance", 10.0)
    }

    #[test]
    fn a_coordinate_near_a_location_is_a_random_vector_from_the_location_or_from_me() {
        // t = pi / 2, u = 0.5: straight along y, 5 up
        let mut rig = base_rig();
        rig.world = crate::env::TestWorld::with_numbers(&[0.25, 0.75]);
        rig.world.add_ball(1, [100.0, 0.0, 0.0], 500);
        rig.post(ScopeType::Item, "Location", Value::None);
        assert_eq!(rig.run(Box::new(SelectCoordinateNearLocationAction), near_attrs()), Status::Success);
        close(&rig.last(ScopeType::Item, "Chosen"), [100.0, 10.0 * 0.75f64.sqrt(), 5.0]);
        // a location of the universe
        let mut rig = base_rig();
        rig.world = crate::env::TestWorld::with_numbers(&[0.25, 0.75, 0.5]);
        rig.world.add_ball(1, [100.0, 0.0, 0.0], 500);
        rig.world.location_positions.insert(60003760, [1000.0, 2000.0, 3000.0]);
        rig.post(ScopeType::Item, "Location", Value::Int(60003760));
        // a shell of 4 below the 10: the radius is 10 - 0.5 * 4
        let attrs = near_attrs().with("minDistance", 6.0);
        rig.run(Box::new(SelectCoordinateNearLocationAction), attrs);
        close(&rig.last(ScopeType::Item, "Chosen"), [1000.0, 2000.0 + 8.0 * 0.75f64.sqrt(), 3000.0 + 4.0]);
        // a location that is not known
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Location", Value::Int(1));
        rig.run(Box::new(SelectCoordinateNearLocationAction), near_attrs());
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Key(_)))));
    }

    fn near_coordinate_attrs() -> Attrs {
        Attrs::new()
            .with("sourceCoordinateAddress", item("Source"))
            .with("targetBallAddress", item("Ball"))
            .with("destinationCoordinateAddress", item("Chosen"))
            .with("maxDistance", 10.0)
            .with("minDistance", 10.0)
    }

    #[test]
    fn a_coordinate_near_a_coordinate_or_a_ball_falls_back_on_where_i_am() {
        let numbers = [0.25, 0.75];
        let mut rig = base_rig();
        rig.world = crate::env::TestWorld::with_numbers(&numbers);
        rig.world.add_ball(1, [100.0, 0.0, 0.0], 500);
        rig.post(ScopeType::Item, "Source", Value::from_vec3([0.0, 0.0, 50.0]));
        rig.run(Box::new(SelectCoordinateNearCoordinateAction::near_coordinate()), near_coordinate_attrs());
        close(&rig.last(ScopeType::Item, "Chosen"), [0.0, 10.0 * 0.75f64.sqrt(), 55.0]);
        let mut rig = base_rig();
        rig.world = crate::env::TestWorld::with_numbers(&numbers);
        rig.world.add_ball(1, [100.0, 0.0, 0.0], 500);
        rig.post(ScopeType::Item, "Source", Value::None);
        rig.run(Box::new(SelectCoordinateNearCoordinateAction::near_coordinate()), near_coordinate_attrs());
        close(&rig.last(ScopeType::Item, "Chosen"), [100.0, 10.0 * 0.75f64.sqrt(), 5.0]);
        // near a ball
        let mut rig = base_rig();
        rig.world = crate::env::TestWorld::with_numbers(&numbers);
        rig.world.add_ball(2, [300.0, 0.0, 0.0], 501);
        rig.post(ScopeType::Item, "Ball", Value::Int(2));
        assert_eq!(
            rig.run(Box::new(SelectCoordinateNearCoordinateAction::near_ball()), near_coordinate_attrs()),
            Status::Success
        );
        close(&rig.last(ScopeType::Item, "Chosen"), [300.0, 10.0 * 0.75f64.sqrt(), 5.0]);
        // a ball that is not there
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Ball", Value::Int(99));
        rig.run(Box::new(SelectCoordinateNearCoordinateAction::near_ball()), near_coordinate_attrs());
        assert!(rig.last_error.is_some());
        assert_eq!(SelectCoordinateNearCoordinateAction::near_ball().type_name(), "SelectCoordinateNearBall");
    }

    // ---- ApproachCoordinatesAction, ApproachObject ----

    fn approach_attrs(blocking: bool) -> Attrs {
        Attrs::new()
            .with("coordinateAddress", item("Where"))
            .with("blocking", blocking)
            .with("notifyRange", 500.0)
    }

    #[test]
    fn a_ball_goes_to_the_coordinates_and_when_it_blocks_the_task_waits_for_the_ball_to_come_in_range() {
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([1.0, 2.0, 3.0]));
        assert_eq!(rig.run(Box::new(ApproachCoordinatesAction), approach_attrs(false)), Status::Success);
        assert_eq!(rig.world.take_calls(), ["goto_point(1, [1.0, 2.0, 3.0])"]);
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([1.0, 2.0, 3.0]));
        assert_eq!(rig.run(Box::new(ApproachCoordinatesAction), approach_attrs(true)), Status::Suspended);
        assert_eq!(rig.world.take_calls(), ["goto_point_notify(1, [1.0, 2.0, 3.0], 500)"]);
        let observer = rig.world.notify_observers[0];
        rig.call_observer(observer, &[Value::Bool(true)]);
        assert_eq!(rig.world.take_calls(), ["stop_movement_events(1)"]);
        assert_eq!(rig.task_status(), Status::Success);
    }

    #[test]
    fn no_coordinates_is_a_failure_and_a_ball_that_cannot_go_is_a_fault() {
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Where", Value::None);
        assert_eq!(rig.run(Box::new(ApproachCoordinatesAction), approach_attrs(false)), Status::Failure);
        assert!(rig.world.take_calls().is_empty());
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([1.0, 2.0, 3.0]));
        rig.world.failing_balls.insert(1);
        rig.run(Box::new(ApproachCoordinatesAction), approach_attrs(false));
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Runtime(_)))));
    }

    fn approach_object_attrs(blocking: bool) -> Attrs {
        Attrs::new()
            .with("itemIdAddress", item("Item"))
            .with("blocking", blocking)
            .with("notifyRange", 500.0)
            .with("approachRange", 1000.0)
    }

    #[test]
    fn an_object_is_approached_to_a_range_and_the_notify_range_is_never_below_it() {
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Item", Value::Int(2));
        assert_eq!(rig.run(Box::new(ApproachObject), approach_object_attrs(false)), Status::Success);
        assert_eq!(rig.world.take_calls(), ["approach_object(1, 2, 1000, false)"]);
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Item", Value::Int(2));
        assert_eq!(rig.run(Box::new(ApproachObject), approach_object_attrs(true)), Status::Suspended);
        assert_eq!(rig.world.take_calls(), ["approach_object_notify(1, 2, 1000, 1000)"]);
        let observer = rig.world.notify_observers[0];
        rig.call_observer(observer, &[Value::Bool(true)]);
        assert_eq!(rig.task_status(), Status::Success);
        // a notify range that is more is kept; the range can come from the blackboard
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Item", Value::Int(2));
        rig.post(ScopeType::Item, "Range", Value::Float(100.0));
        let attrs = approach_object_attrs(true).with("approachRangeAddress", item("Range"));
        rig.run(Box::new(ApproachObject), attrs);
        assert_eq!(rig.world.take_calls(), ["approach_object_notify(1, 2, 500, 100)"]);
    }

    #[test]
    fn no_object_or_one_that_is_not_in_the_park_is_a_failure() {
        for item_id in [Value::None, Value::Int(77)] {
            let mut rig = base_rig();
            rig.post(ScopeType::Item, "Item", item_id);
            assert_eq!(rig.run(Box::new(ApproachObject), approach_object_attrs(false)), Status::Failure);
            assert!(rig.world.take_calls().is_empty());
        }
    }

    // ---- AlignToCoordinatesAction ----

    fn align_attrs() -> Attrs {
        Attrs::new().with("coordinateAddress", item("Where"))
    }

    #[test]
    fn aligning_starts_the_clock_and_makes_the_point_at_least_30_km_away() {
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([1100.0, 0.0, 0.0]));
        rig.shared.now = 777;
        assert_eq!(rig.run(Box::new(AlignToCoordinatesAction), align_attrs()), Status::Running);
        assert_eq!(
            rig.last(ScopeType::Item, "ALIGN_START_TIME"),
            Value::Int(777 + 1_000_000),
            "the tick is a tenth of a second"
        );
        close(&rig.last(ScopeType::Item, "Where"), [30100.0, 0.0, 0.0]);
        // a point that is far enough is left alone
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([50100.0, 0.0, 0.0]));
        rig.run(Box::new(AlignToCoordinatesAction), align_attrs());
        assert_eq!(rig.messages_on("Where"), 1);
    }

    /// The ball flies along `velocity`; the point is along x.
    fn align_rig(velocity: [f64; 3]) -> Rig {
        let mut rig = base_rig();
        rig.world.balls.get_mut(&1).unwrap().velocity = velocity;
        rig.post(ScopeType::Item, "Where", Value::from_vec3([50100.0, 0.0, 0.0]));
        rig
    }

    #[test]
    fn a_ship_that_flies_towards_the_point_is_aligned_and_stops() {
        let mut rig = align_rig([100.0, 0.0, 0.0]);
        assert_eq!(rig.run(Box::new(AlignToCoordinatesAction), align_attrs()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["goto_point(1, [5100.0, 0.0, 0.0])", "stop_movement(1)"]);
        // half a degree off is in; two degrees off is not, unless the task says
        let half = 0.5f64.to_radians();
        let mut rig = align_rig([half.cos(), half.sin(), 0.0]);
        assert_eq!(rig.run(Box::new(AlignToCoordinatesAction), align_attrs()), Status::Success);
        let two = 2.0f64.to_radians();
        let mut rig = align_rig([two.cos(), two.sin(), 0.0]);
        assert_eq!(rig.run(Box::new(AlignToCoordinatesAction), align_attrs()), Status::Running);
        let mut rig = align_rig([two.cos(), two.sin(), 0.0]);
        let attrs = align_attrs().with("acceptableAngleDeviationDegrees", 3.0);
        assert_eq!(rig.run(Box::new(AlignToCoordinatesAction), attrs), Status::Success);
    }

    #[test]
    fn a_ship_that_does_not_align_in_time_fails_and_stops() {
        let mut rig = align_rig([0.0, 100.0, 0.0]);
        assert_eq!(rig.run(Box::new(AlignToCoordinatesAction), align_attrs()), Status::Running);
        // 60 s is the limit; a task that says 5 s
        rig.advance_seconds(59.0);
        rig.tick().unwrap();
        assert_eq!(rig.task_status(), Status::Running);
        rig.advance_seconds(2.0);
        rig.world.take_calls();
        rig.tick().unwrap();
        assert_eq!(rig.task_status(), Status::Failure);
        assert_eq!(rig.world.take_calls().last().unwrap(), "stop_movement(1)");
        // exactly 60 s is not more than 60 s
        let mut rig = align_rig([0.0, 100.0, 0.0]);
        rig.run(Box::new(AlignToCoordinatesAction), align_attrs());
        rig.advance_seconds(59.9);
        rig.tick().unwrap();
        assert_eq!(rig.task_status(), Status::Running);
        rig.tick().unwrap();
        assert_eq!(rig.task_status(), Status::Failure);
        let mut rig = align_rig([0.0, 100.0, 0.0]);
        rig.run(Box::new(AlignToCoordinatesAction), align_attrs().with("timeoutSeconds", 5.0));
        rig.advance_seconds(6.0);
        rig.tick().unwrap();
        assert_eq!(rig.task_status(), Status::Failure);
    }

    #[test]
    fn a_point_that_is_gone_fails_the_task_and_the_comparison_that_follows_is_a_fault() {
        let mut rig = align_rig([100.0, 0.0, 0.0]);
        rig.run(
            Box::new(AlignToCoordinatesAction),
            align_attrs().with("acceptableAngleDeviationDegrees", -1.0),
        );
        assert_eq!(rig.task_status(), Status::Running);
        rig.post(ScopeType::Item, "Where", Value::None);
        let error = rig.tick().unwrap_err();
        assert!(matches!(error, BehaviorError::Task(ref m) if m.starts_with("TypeError")), "{error:?}");
        assert_eq!(rig.task_status(), Status::Failure);
    }

    #[test]
    fn an_align_task_that_is_still_running_is_not_invalidated_by_the_clean_up() {
        let mut rig = align_rig([0.0, 100.0, 0.0]);
        rig.run(Box::new(AlignToCoordinatesAction), align_attrs());
        rig.clean_up_task();
        assert_eq!(rig.task_status(), Status::Running);
        let mut rig = align_rig([100.0, 0.0, 0.0]);
        rig.run(Box::new(AlignToCoordinatesAction), align_attrs());
        assert_eq!(rig.task_status(), Status::Success);
        rig.clean_up_task();
        assert_eq!(rig.task_status(), Status::Invalid);
    }

    // ---- OrbitAtDistanceAction ----

    fn orbit_attrs(blocking: bool) -> Attrs {
        Attrs::new()
            .with("orbitTargetAddress", item("Target"))
            .with("orbitRange", 2000.0)
            .with("blocking", blocking)
    }

    fn orbit_rig() -> Rig {
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Target", Value::Int(2));
        rig
    }

    #[test]
    fn an_orbit_is_started_and_when_it_blocks_the_task_waits_until_the_ball_is_in_range() {
        let mut rig = orbit_rig();
        assert_eq!(rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(false)), Status::Success);
        assert_eq!(rig.world.take_calls(), ["orbit_object(1, 2, 2000)"]);
        let mut rig = orbit_rig();
        assert_eq!(
            rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(true).with("blockUntilRange", 3000.0)),
            Status::Suspended
        );
        assert_eq!(rig.world.take_calls(), ["orbit_object(1, 2, 2000, 3000)"]);
        let observer = rig.world.notify_observers[0];
        rig.call_observer(observer, &[Value::Bool(true)]);
        assert_eq!(rig.task_status(), Status::Success);
        // a range to block until of nothing is 1000, and one on the blackboard is that
        let mut rig = orbit_rig();
        rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(true).with("blockUntilRange", 0));
        assert_eq!(rig.world.take_calls(), ["orbit_object(1, 2, 2000, 1000)"]);
        let mut rig = orbit_rig();
        rig.post(ScopeType::Item, "Until", Value::Float(1234.0));
        rig.run(
            Box::new(OrbitAtDistanceAction),
            orbit_attrs(true).with("blockUntilRangeAddress", item("Until")),
        );
        assert_eq!(rig.world.take_calls(), ["orbit_object(1, 2, 2000, 1234)"]);
    }

    #[test]
    fn the_orbit_range_is_the_blackboards_before_the_tasks_and_without_either_the_task_is_badly_made() {
        let mut rig = orbit_rig();
        rig.post(ScopeType::Item, "Range", Value::Float(750.0));
        rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(false).with("orbitRangeAddress", item("Range")));
        assert_eq!(rig.world.take_calls(), ["orbit_object(1, 2, 750)"]);
        let mut rig = orbit_rig();
        let attrs = Attrs::new().with("orbitTargetAddress", item("Target")).with("blocking", false);
        rig.run(Box::new(OrbitAtDistanceAction), attrs);
        assert!(matches!(rig.last_error, Some(BehaviorError::Authoring(_))));
    }

    #[test]
    fn a_velocity_fraction_is_set_after_the_orbit_and_undone_when_the_task_is_cleaned_up() {
        let mut rig = orbit_rig();
        rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(false).with("velocityFraction", 0.5));
        assert_eq!(rig.world.take_calls(), ["orbit_object(1, 2, 2000)", "set_speed_fraction(1, 0.5)"]);
        rig.clean_up_task();
        assert_eq!(rig.world.take_calls(), ["set_speed_fraction(1, 1)"]);
        let mut rig = orbit_rig();
        rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(false));
        rig.world.take_calls();
        rig.clean_up_task();
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn no_target_a_target_that_is_gone_or_a_user_error_of_the_orbit_is_a_failure_and_a_released_ball_is_unrecoverable()
     {
        for target in [Value::None, Value::Int(77)] {
            let mut rig = orbit_rig();
            rig.post(ScopeType::Item, "Target", target);
            assert_eq!(rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(false)), Status::Failure);
        }
        let mut rig = orbit_rig();
        rig.world.orbit_error = Some(WorldError::User("Nope".into()));
        assert_eq!(rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(false)), Status::Failure);
        assert_eq!(rig.last_error, None);
        // the fraction is not set for an orbit that failed
        let mut rig = orbit_rig();
        rig.world.orbit_error = Some(WorldError::User("Nope".into()));
        assert_eq!(
            rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(false).with("velocityFraction", 0.5)),
            Status::Failure
        );
        assert!(rig.world.take_calls().is_empty());
        let mut rig = orbit_rig();
        rig.world.orbit_error = Some(WorldError::Runtime("boom".into()));
        rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(false));
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Runtime(_)))));
        let mut rig = orbit_rig();
        rig.world.released_balls.insert(1);
        rig.run(Box::new(OrbitAtDistanceAction), orbit_attrs(false));
        assert!(matches!(rig.last_error, Some(BehaviorError::Unrecoverable(_))));
        assert!(rig.world.take_calls().is_empty());
    }

    // ---- the small ones ----

    #[test]
    fn a_ball_stops_says_where_it_goes_says_where_it_is_and_goes_where_it_is_told() {
        let mut rig = base_rig();
        assert_eq!(rig.run(Box::new(FullStopAction), Attrs::new()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["stop_movement(1)"]);
        let mut rig = base_rig();
        rig.world.balls.get_mut(&2).unwrap().goto = [7.0, 8.0, 9.0];
        rig.post(ScopeType::Item, "Ball", Value::Int(2));
        let attrs = Attrs::new().with("ballIdAddress", item("Ball")).with("gotoPointAddress", item("Goto"));
        assert_eq!(rig.run(Box::new(GetBallGotoPointAction), attrs.clone()), Status::Success);
        assert_eq!(rig.last(ScopeType::Item, "Goto"), Value::from_vec3([7.0, 8.0, 9.0]));
        rig.post(ScopeType::Item, "Ball", Value::Int(99));
        rig.run(Box::new(GetBallGotoPointAction), attrs);
        assert!(rig.last_error.is_some());
        let mut rig = base_rig();
        assert_eq!(
            rig.run(Box::new(PostOwnPosition), Attrs::new().with("coordinateAddress", item("Me"))),
            Status::Success
        );
        assert_eq!(rig.last(ScopeType::Item, "Me"), Value::from_vec3([100.0, 0.0, 0.0]));
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([4.0, 5.0, 6.0]));
        assert_eq!(
            rig.run(Box::new(GotoPosition), Attrs::new().with("coordinateAddress", item("Where"))),
            Status::Success
        );
        assert_eq!(rig.world.take_calls(), ["goto_point(1, [4.0, 5.0, 6.0])"]);
        rig.post(ScopeType::Item, "Where", Value::None);
        rig.run(Box::new(GotoPosition), Attrs::new().with("coordinateAddress", item("Where")));
        assert!(matches!(rig.last_error, Some(BehaviorError::Task(ref m)) if m.starts_with("TypeError")));
    }

    #[test]
    fn a_ball_or_an_item_is_moved_to_a_location_that_is_there() {
        let attrs = Attrs::new().with("locationAddress", item("Where")).with("itemIdAddress", item("Item"));
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([4.0, 5.0, 6.0]));
        assert_eq!(rig.run(Box::new(TransferToLocation), attrs.clone()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["change_ball_position(1, [4.0, 5.0, 6.0])"]);
        rig.post(ScopeType::Item, "Where", Value::None);
        assert_eq!(rig.run(Box::new(TransferToLocation), attrs.clone()), Status::Failure);
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([4.0, 5.0, 6.0]));
        rig.post(ScopeType::Item, "Item", Value::Int(2));
        assert_eq!(rig.run(Box::new(TransferItemToLocation), attrs.clone()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["change_ball_position(2, [4.0, 5.0, 6.0])"]);
        for (location, item_id) in [(Value::None, Value::Int(2)), (Value::from_vec3([1.0; 3]), Value::None)] {
            let mut rig = base_rig();
            rig.post(ScopeType::Item, "Where", location);
            rig.post(ScopeType::Item, "Item", item_id);
            assert_eq!(rig.run(Box::new(TransferItemToLocation), attrs.clone()), Status::Failure);
            assert_eq!(rig.last_error, None);
            assert!(rig.world.take_calls().is_empty());
        }
    }

    #[test]
    fn a_ship_is_made_invulnerable_and_vulnerable_and_cloaked_and_uncloaked() {
        let attrs = Attrs::new().with("reason", "Test");
        let mut rig = base_rig();
        assert_eq!(rig.run(Box::new(MakeSelfInvulnerable), attrs.clone()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["make_invulnerable_permanently(1, Test)"]);
        let mut rig = base_rig();
        assert_eq!(rig.run(Box::new(CancelInvulnerabilityForSelf), attrs), Status::Success);
        assert_eq!(rig.world.take_calls(), ["cancel_current_invulnerability(1, Test)"]);
        let mut rig = base_rig();
        assert_eq!(rig.run(Box::new(Cloak), Attrs::new()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["cloak_ship(1)"]);
        let mut rig = base_rig();
        assert_eq!(rig.run(Box::new(Uncloak), Attrs::new()), Status::Success);
        assert_eq!(rig.world.take_calls(), ["uncloak_ship(1)"]);
    }

    #[test]
    fn coordinates_are_found_from_a_direction_and_a_distance_or_reduced_towards_me() {
        let mut rig = base_rig();
        rig.post(ScopeType::Item, "Direction", Value::from_vec3([0.0, 10.0, 0.0]));
        let attrs = Attrs::new()
            .with("directionAddress", item("Direction"))
            .with("coordinatesAddress", item("Out"))
            .with("distance", 50.0);
        assert_eq!(rig.run(Box::new(SelectCoordinatesFromDirectionIntoDistance), attrs), Status::Success);
        assert_eq!(
            rig.last(ScopeType::Item, "Out"),
            Value::List(vec![Value::Float(100.0), Value::Float(50.0), Value::Float(0.0)])
        );
        let reduce = |distance: Option<f64>| {
            let mut rig = base_rig();
            rig.post(ScopeType::Item, "Source", Value::from_vec3([300.0, 0.0, 0.0]));
            rig.post(ScopeType::Item, "Distance", distance.map_or(Value::None, Value::Float));
            let attrs = Attrs::new()
                .with("sourceCoordinateAddress", item("Source"))
                .with("distanceAddress", item("Distance"))
                .with("destinationCoordinateAddress", item("Out"));
            let status = rig.run(Box::new(GetCoordinatesReducedByDistance), attrs);
            (status, rig.last(ScopeType::Item, "Out"))
        };
        // I am at 100, the point is at 300: 50 nearer to me is 250
        assert_eq!(reduce(Some(50.0)), (Status::Success, Value::from_vec3([250.0, 0.0, 0.0])));
        assert_eq!(reduce(None), (Status::Success, Value::from_vec3([300.0, 0.0, 0.0])));
        assert_eq!(reduce(Some(0.0)), (Status::Success, Value::from_vec3([300.0, 0.0, 0.0])));
    }

    #[test]
    fn a_ship_flies_in_a_random_direction_at_a_fraction_of_its_speed_until_it_is_cleaned_up() {
        let mut rig = base_rig();
        rig.world = crate::env::TestWorld::with_numbers(&[0.25, 1.0]);
        rig.world.add_ball(1, [0.0; 3], 500);
        assert_eq!(
            rig.run(Box::new(FlyIntoRandomDirection), Attrs::new().with("velocityFraction", 0.25)),
            Status::Suspended
        );
        let calls = rig.world.take_calls();
        assert!(calls[0].starts_with("goto_direction(1, [0.0"), "{calls:?}");
        assert_eq!(calls[1], "set_speed_fraction(1, 0.25)");
        rig.clean_up_task();
        assert_eq!(rig.world.take_calls(), ["set_speed_fraction(1, 1)"]);
    }

    // ---- ManageMicroWarpDrive ----

    fn mwd_attrs() -> Attrs {
        Attrs::new()
            .with("targetAddress", item("Target"))
            .with("orbitRangeAddress", item("Orbit"))
            .with("microWarpDriveDuration", 5000)
            .with("microWarpDriveChance", 0.5)
    }

    /// I am 5000 m from 2; the orbit is at 10 km, so the drive is for distances of 20 km and more (`2 * 10000`); the rig starts with what makes a turn
    /// want the drive.
    fn mwd_rig() -> Rig {
        let mut rig = base_rig();
        rig.world = crate::env::TestWorld::with_numbers(&[0.0]);
        rig.world.add_ball(1, [0.0; 3], 500);
        rig.world.add_ball(2, [30000.0; 3], 501);
        rig.world.set_dist(1, 2, 30000.0);
        rig.post(ScopeType::Item, "Target", Value::Int(2));
        rig.post(ScopeType::Item, "Orbit", Value::Float(10000.0));
        rig
    }

    #[test]
    fn the_micro_warp_drive_is_turned_on_when_the_target_is_far_and_the_dice_say_so_and_off_when_it_is_near()
    {
        let mut rig = mwd_rig();
        assert_eq!(rig.run(Box::new(ManageMicroWarpDrive::to_target()), mwd_attrs()), Status::Success);
        assert!(rig.world.take_calls().is_empty(), "the thread has not run");
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["set_movement_speed(1, false)"]);
        // the next turn is 5 s on (5000 ms / 1000); the drive is on already
        rig.deliver_scheduled();
        assert!(rig.world.take_calls().is_empty(), "not yet");
        rig.advance_seconds(5.0);
        rig.deliver_scheduled();
        assert!(rig.world.take_calls().is_empty(), "the drive is on and should be");
        rig.world.set_dist(1, 2, 100.0);
        rig.advance_seconds(5.0);
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["set_movement_speed(1, true)"]);
        assert_eq!(rig.last_error, None);
    }

    #[test]
    fn a_die_that_is_high_keeps_the_drive_off_and_a_short_orbit_needs_less_distance() {
        let mut rig = mwd_rig();
        rig.world = crate::env::TestWorld::with_numbers(&[0.9]);
        rig.world.add_ball(1, [0.0; 3], 500);
        rig.world.add_ball(2, [30000.0; 3], 501);
        rig.world.set_dist(1, 2, 30000.0);
        rig.run(Box::new(ManageMicroWarpDrive::to_target()), mwd_attrs());
        rig.deliver_scheduled();
        assert!(rig.world.take_calls().is_empty());
        // an orbit of 2000: 3000 (1.5 times), but no less than 1500; one of 500: 750 becomes 1500
        for (orbit, distance, on) in [
            (2000.0, 3000.0, true),
            (2000.0, 2999.0, false),
            (500.0, 1500.0, true),
            (500.0, 1499.0, false),
            (5000.0, 10000.0, true),
            (5000.0, 9999.0, false),
            (100000.0, 20000.0, true),
            (100000.0, 19999.0, false),
        ] {
            let mut rig = mwd_rig();
            rig.post(ScopeType::Item, "Orbit", Value::Float(orbit));
            rig.world.set_dist(1, 2, distance);
            rig.run(Box::new(ManageMicroWarpDrive::to_target()), mwd_attrs());
            rig.deliver_scheduled();
            assert_eq!(rig.world.take_calls().len(), usize::from(on), "orbit {orbit}, distance {distance}");
        }
    }

    #[test]
    fn a_range_on_the_blackboard_is_the_range_of_the_drive() {
        let mut rig = mwd_rig();
        rig.post(ScopeType::Item, "Range", Value::Float(1000.0));
        rig.world.set_dist(1, 2, 1000.0);
        rig.run(
            Box::new(ManageMicroWarpDrive::to_target()),
            mwd_attrs().with("microwarpdriveRangeAddress", item("Range")),
        );
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["set_movement_speed(1, false)"]);
    }

    #[test]
    fn the_thread_ends_with_the_balls_and_the_task_and_is_started_once() {
        let mut rig = mwd_rig();
        rig.run(Box::new(ManageMicroWarpDrive::to_target()), mwd_attrs());
        assert_eq!(rig.shared.scheduled_call_count(), 1);
        rig.deliver_scheduled();
        assert_eq!(rig.shared.scheduled_call_count(), 1, "the next turn");
        rig.world.balls.remove(&2);
        rig.advance_seconds(5.0);
        rig.deliver_scheduled();
        assert_eq!(rig.shared.scheduled_call_count(), 0, "the target is gone");
        // a cloaked target
        let mut rig = mwd_rig();
        rig.run(Box::new(ManageMicroWarpDrive::to_target()), mwd_attrs());
        rig.world.cloaked.insert(2);
        rig.deliver_scheduled();
        assert_eq!(rig.shared.scheduled_call_count(), 0);
        assert!(rig.world.take_calls().is_empty());
    }

    #[test]
    fn a_task_that_is_cleaned_up_turns_the_drive_off_and_ends_its_thread() {
        let mut rig = mwd_rig();
        rig.run(Box::new(ManageMicroWarpDrive::to_target()), mwd_attrs());
        rig.deliver_scheduled();
        rig.world.take_calls();
        rig.clean_up_task();
        assert_eq!(rig.world.take_calls(), ["set_movement_speed(1, true)"]);
        rig.advance_seconds(5.0);
        rig.deliver_scheduled();
        assert!(rig.world.take_calls().is_empty());
        assert_eq!(rig.shared.scheduled_call_count(), 0);
    }

    #[test]
    fn no_target_or_a_target_in_another_bubble_is_a_failure() {
        let mut rig = mwd_rig();
        rig.post(ScopeType::Item, "Target", Value::None);
        assert_eq!(rig.run(Box::new(ManageMicroWarpDrive::to_target()), mwd_attrs()), Status::Failure);
        assert_eq!(rig.shared.scheduled_call_count(), 0);
        let mut rig = mwd_rig();
        rig.world.balls.get_mut(&2).unwrap().new_bubble_id = 9;
        assert_eq!(rig.run(Box::new(ManageMicroWarpDrive::to_target()), mwd_attrs()), Status::Failure);
        assert_eq!(rig.shared.scheduled_call_count(), 0);
    }

    #[test]
    fn a_drive_to_coordinates_goes_by_where_i_am_and_the_bubble_of_the_point() {
        let attrs = Attrs::new()
            .with("coordinateAddress", item("Where"))
            .with("orbitRangeAddress", item("Orbit"))
            .with("microWarpDriveDuration", 5000)
            .with("microWarpDriveChance", 0.5);
        let mut rig = mwd_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([30000.0, 0.0, 0.0]));
        rig.world.bubble_of_position.insert([30000, 0, 0], 0);
        assert_eq!(rig.run(Box::new(ManageMicroWarpDrive::to_coordinates()), attrs.clone()), Status::Success);
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["set_movement_speed(1, false)"]);
        // a point in another bubble
        let mut rig = mwd_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([30000.0, 0.0, 0.0]));
        rig.world.bubble_of_position.insert([30000, 0, 0], 8);
        assert_eq!(rig.run(Box::new(ManageMicroWarpDrive::to_coordinates()), attrs.clone()), Status::Failure);
        // the thread of a drive to a point does not care about a target
        let mut rig = mwd_rig();
        rig.post(ScopeType::Item, "Where", Value::from_vec3([30000.0, 0.0, 0.0]));
        rig.world.bubble_of_position.insert([30000, 0, 0], 0);
        rig.run(Box::new(ManageMicroWarpDrive::to_coordinates()), attrs);
        rig.world.balls.remove(&2);
        rig.deliver_scheduled();
        assert_eq!(rig.world.take_calls(), ["set_movement_speed(1, false)"]);
    }

    #[test]
    fn a_duration_of_whole_milliseconds_is_divided_by_a_thousand_as_a_whole_number() {
        let mut rig = mwd_rig();
        rig.run(Box::new(ManageMicroWarpDrive::to_target()), mwd_attrs().with("microWarpDriveDuration", 500));
        rig.deliver_scheduled();
        // 500 / 1000 is 0 for the client: the next turn is due at once
        assert_eq!(rig.shared.next_call_due(), Some(rig.shared.now));
        let mut rig = mwd_rig();
        rig.run(
            Box::new(ManageMicroWarpDrive::to_target()),
            mwd_attrs().with("microWarpDriveDuration", 2500.0),
        );
        rig.deliver_scheduled();
        assert_eq!(rig.shared.next_call_due(), Some(rig.shared.now + 25_000_000));
    }

    // ---- ModifyOrbitVelocity ----

    fn velocity_rig() -> Rig {
        let mut rig = base_rig();
        rig.world.groups.insert(10, TestGroup { members: vec![1, 3, 4], ..Default::default() });
        rig.world
            .items
            .insert(3, crate::env::InventoryItem { item_id: 3, type_id: 700, ..Default::default() });
        rig.world
            .items
            .insert(4, crate::env::InventoryItem { item_id: 4, type_id: 701, ..Default::default() });
        rig.world
            .items
            .insert(1, crate::env::InventoryItem { item_id: 1, type_id: 700, ..Default::default() });
        rig
    }

    #[test]
    fn a_custom_velocity_is_set_only_when_the_velocity_that_was_worked_out_is_nothing() {
        // not the commander, and no velocity: set
        let mut rig = velocity_rig();
        assert_eq!(
            rig.run(
                Box::new(ModifyOrbitVelocity::default()),
                Attrs::new().with("orbitVelocity", Value::None)
            ),
            Status::Success
        );
        assert_eq!(rig.world.take_calls(), ["set_custom_orbit_velocity(1, None)"]);
        // a velocity that is there is not set (the client's `if not customVelocity`)
        let mut rig = velocity_rig();
        rig.run(Box::new(ModifyOrbitVelocity::default()), Attrs::new().with("orbitVelocity", 250.0));
        assert!(rig.world.take_calls().is_empty());
        assert_eq!(rig.task_status(), Status::Success);
    }

    /// Runs `ModifyOrbitVelocity` as the commander (1) of the group 3 (a dps, cruising at 400) and 4 (a dps, whose top speed is 300), and 5 (a
    /// logistic that is slower still and not for combat).
    fn run_as_commander(setup: impl FnOnce(&mut Rig) + 'static) -> Rig {
        let (rig, _) = with_task(
            move |rig| {
                *rig = velocity_rig();
                rig.world.groups.insert(10, TestGroup { members: vec![1, 3, 4, 5], ..Default::default() });
                rig.world
                    .items
                    .insert(5, crate::env::InventoryItem { item_id: 5, type_id: 702, ..Default::default() });
                rig.world.type_attributes.insert((700, ATTRIBUTE_ENTITY_CRUISE_SPEED), 400.0);
                rig.world.type_attributes.insert((700, ATTRIBUTE_MAX_VELOCITY), 900.0);
                rig.world.type_attributes.insert((701, ATTRIBUTE_MAX_VELOCITY), 300.0);
                rig.world.type_attributes.insert((702, ATTRIBUTE_MAX_VELOCITY), 100.0);
                setup(rig);
            },
            |t| {
                t.send_blackboard_value(
                    &crate::constants::blackboardchannels::commander_address(),
                    Value::Int(1),
                );
                for (item_id, role) in [(1, "commander"), (3, "dps"), (4, "dps"), (5, "logistic")] {
                    let channel = t.channel_for_item(item_id, "ROLE");
                    t.channel_send(&channel, Value::Str(role.into()));
                }
                t.set_attribute("orbitVelocity", Value::None);
                ModifyOrbitVelocity::default().on_enter(t);
            },
        );
        rig
    }

    #[test]
    fn the_commander_goes_at_a_fraction_of_the_slowest_of_the_fighters_and_a_group_without_speed_is_badly_made()
     {
        // the commander has the role of a commander, which is no combat role: 3 cruises at 400 and 4 tops at 300; 5 is a logistic
        let mut rig = run_as_commander(|_| {});
        assert_eq!(rig.last_error, None);
        // the velocity is not nothing: the client leaves the ball alone
        assert!(rig.world.take_calls().is_empty());
        // nobody with a speed
        let rig = run_as_commander(|rig| rig.world.type_attributes.clear());
        assert!(matches!(rig.last_error, Some(BehaviorError::Authoring(_))));
        // a member that is not in the inventory
        let rig = run_as_commander(|rig| {
            rig.world.items.remove(&4);
        });
        assert!(matches!(rig.last_error, Some(BehaviorError::World(WorldError::Runtime(_)))));
    }

    #[test]
    fn the_task_that_set_a_velocity_takes_it_away_when_it_is_cleaned_up() {
        let mut rig = velocity_rig();
        rig.run(Box::new(ModifyOrbitVelocity::default()), Attrs::new().with("orbitVelocity", 250.0));
        rig.world.take_calls();
        rig.clean_up_task();
        assert_eq!(rig.world.take_calls(), ["remove_custom_orbit_velocity(1)"]);
        let mut rig = velocity_rig();
        rig.run(Box::new(ModifyOrbitVelocity::default()), Attrs::new().with("orbitVelocity", Value::None));
        rig.world.take_calls();
        rig.clean_up_task();
        assert!(rig.world.take_calls().is_empty());
    }
}
