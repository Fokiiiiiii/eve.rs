//! The server's `ballpark` as the tasks see it: balls and their slim items, bubbles, the state of a ship in space, and what a task can ask a ball
//! to do. The server's ballpark is not part of the client; only what the tasks use of it is here, with the names of the client's methods in snake
//! case. What the client's calls raise is `WorldError`.

use super::{WorldError, WorldResult};
use crate::value::Value;

/// A ball of the ballpark: what the tasks read of it (`ball.x`, `ball.mode`, `ball.newBubbleId`, ...).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Ball {
    pub id: i64,
    pub position: [f64; 3],
    pub velocity: [f64; 3],
    /// `gotoX`, `gotoY`, `gotoZ`: where a ball that was told to go somewhere goes.
    pub goto: [f64; 3],
    pub mode: i64,
    /// `newBubbleId`.
    pub new_bubble_id: i64,
    pub follow_id: i64,
    pub radius: f64,
    pub is_cloaked: bool,
    pub is_free: bool,
    /// `miniBalls`: the spheres a big ball is made of, placed from its centre.
    pub mini_balls: Vec<MiniBall>,
}

/// One of the spheres (`ball.miniBalls`) a ball is made of.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct MiniBall {
    pub position: [f64; 3],
    pub radius: f64,
}

/// A slim item: what the tasks read of an item of the ballpark.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct SlimItem {
    pub item_id: i64,
    pub type_id: i64,
    pub group_id: i64,
    pub category_id: i64,
    pub owner_id: Option<i64>,
    pub char_id: Option<i64>,
}

/// A proximity sensor component of an item (`componentRegistry.GetComponentForItem(id, PROXIMITY_SENSOR_CLASS)`): where it is and what it sees.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct ProximitySensor {
    pub item_id: i64,
    /// `GetBubbleId()`.
    pub bubble_id: i64,
    /// `GetBallIdsInRange()`.
    pub ball_ids_in_range: Vec<i64>,
}

/// The settings of a fleet warp formation (`fleet_formation_settings`).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct FormationSettings {
    pub formation_type: Value,
    pub formation_spacing: Value,
    pub formation_size: Value,
}

/// What a formation picker answers.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Formation {
    /// `GetDestinationVectors()`: where each ship goes.
    pub destination_coordinates: Vec<(i64, [f64; 3])>,
    /// `GetJitterRadius()`.
    pub jitter_radius: f64,
    /// `GetMinimumRange()`.
    pub minimum_range: f64,
    /// `GetMinWarpSpeed()`.
    pub min_warp_speed: f64,
    /// `GetFleetCandidates()`: the ships that warp and the characters that fly them.
    pub fleet_candidates: Vec<(i64, Option<i64>)>,
}

/// The ballpark's answers and what it is asked to do. The default of every method is the answer of a ballpark with nothing in it.
pub trait Ballpark {
    /// `ballpark.HasBall`, and `ball_id in ballpark.balls`.
    fn has_ball(&self, _ball_id: i64) -> bool {
        false
    }

    /// `ballpark.balls.get(ballId)`, `ballpark.balls[ballId]` and `ballpark.GetBall(ballId)`: the ball, or nothing where the client's answer is `None`
    /// or its exception.
    fn ball(&self, _ball_id: i64) -> Option<Ball> {
        None
    }

    /// `ballpark.slims.get(itemId)` and `ballpark.GetSlimItem`.
    fn slim_item(&self, _item_id: i64) -> Option<SlimItem> {
        None
    }

    /// `ballpark.bubbles.get(bubbleId)`: the balls of a bubble, `None` for a bubble that is not there.
    fn ball_ids_in_bubble(&self, _bubble_id: i64) -> Option<Vec<i64>> {
        None
    }

    /// `ballpark.GetShipsInBubble`.
    fn ship_ids_in_bubble(&self, _bubble_id: i64) -> Vec<i64> {
        Vec::new()
    }

    /// `ballpark.globals`: the ids of the balls that are seen from everywhere.
    fn global_ball_ids(&self) -> Vec<i64> {
        Vec::new()
    }

    /// `ballpark.GetSurfaceDist(a, b)`.
    fn surface_dist(&self, _a: i64, _b: i64) -> WorldResult<f64> {
        Err(WorldError::Runtime("no ballpark".into()))
    }

    /// `ballpark.IsCloaked`.
    fn is_cloaked(&self, _ball_id: i64) -> WorldResult<bool> {
        Ok(false)
    }

    /// `ballpark.IsInvulnerable`.
    fn is_invulnerable(&self, _item_id: i64) -> bool {
        false
    }

    /// `ballpark.IsWarping`: the warp state (`shipNotWarping` when the ship is not warping).
    fn is_warping(&self, _ball_id: i64) -> WorldResult<i64> {
        Ok(0)
    }

    /// `ballpark.GetBubbleAtCoordinates`.
    fn bubble_at_coordinates(&self, _position: [f64; 3]) -> i64 {
        -1
    }

    /// `ballpark.GetClosestDisruptorAndIntersectionInWarpPath(from, to)`: the closest warp disruptor in the way and where the path meets it.
    fn closest_disruptor_and_intersection_in_warp_path(
        &self,
        _from: [f64; 3],
        _to: [f64; 3],
    ) -> WorldResult<Option<(i64, [f64; 3])>> {
        Ok(None)
    }

    /// `GetWarpBubbleItemIdsWithinRangeOfCoordinate(ballpark, coordinates)`: `(warp disruption probe ids, warp disruption ship ids)`.
    fn warp_bubble_item_ids_within_range_of_coordinate(&self, _position: [f64; 3]) -> (Vec<i64>, Vec<i64>) {
        (Vec::new(), Vec::new())
    }

    /// `ball.IsVulnerable()` of a structure's ball.
    fn is_structure_vulnerable(&self, _ball_id: i64) -> WorldResult<bool> {
        Err(WorldError::Runtime("no such structure".into()))
    }

    /// `ballpark.dogmaLM.aggressionLM.GetObjectState(itemId).IsAggressed()`.
    fn is_aggressed(&self, _item_id: i64) -> bool {
        false
    }

    // ---- what a ship can be made to do to itself ----

    /// `ballpark.MakeInvulnerablePermanently(ballId, reason)`.
    fn make_invulnerable_permanently(&mut self, _ball_id: i64, _reason: &str) {}

    /// `ballpark.CancelCurrentInvulnerability(ballId, reason)`.
    fn cancel_current_invulnerability(&mut self, _ball_id: i64, _reason: &str) {}

    /// `ballpark.CloakShip(itemId)`.
    fn cloak_ship(&mut self, _item_id: i64) {}

    /// `ballpark.UncloakShip(itemId)`.
    fn uncloak_ship(&mut self, _item_id: i64) {}

    /// `ballpark.DistanceBetween(a, b)`.
    fn distance_between(&self, _a: i64, _b: i64) -> WorldResult<f64> {
        Err(WorldError::Runtime("no ballpark".into()))
    }

    /// `ballpark.solarsystemID`.
    fn solar_system_id(&self) -> Option<i64> {
        None
    }

    /// `ballpark.SetBubbleEvents(bubbleId, on)`: whether the ballpark tells of balls entering and leaving the bubble (`DoBubbleProximity`).
    fn set_bubble_events(&mut self, _bubble_id: i64, _on: bool) {}

    /// `componentRegistry.GetComponentForItem(itemId, PROXIMITY_SENSOR_CLASS)`.
    fn proximity_sensor(&self, _item_id: i64) -> Option<ProximitySensor> {
        None
    }

    /// `componentRegistry.GetInstancesWithComponentClass(PROXIMITY_SENSOR_CLASS)` of those that `HasTags(*tags)`.
    fn proximity_sensors(&self, _tags: &[String]) -> Vec<ProximitySensor> {
        Vec::new()
    }

    /// `componentRegistry.AddComponentInstanceToItem(PROXIMITY_SENSOR_CLASS, itemId, typeId, Bundle(detectionRange=, tags=None))`.
    fn add_proximity_sensor(
        &mut self,
        _item_id: i64,
        _type_id: i64,
        _detection_range: f64,
    ) -> Option<ProximitySensor> {
        None
    }

    /// `componentRegistry.GetComponentForItem(itemId, TURBO_SHIELD_CLASS).IsShieldState(state)`: `None` when the item has no such component
    /// (`KeyError`).
    fn turbo_shield_is_state(&self, _item_id: i64, _state: i64) -> Option<bool> {
        None
    }

    /// `proximitySensor.GetDistanceTo(itemId)` of an item's sensor: `None` when the item has no sensor (`KeyError`).
    fn proximity_sensor_distance_to(&self, _item_id: i64) -> Option<f64> {
        None
    }

    /// `proximitySensor.ReplaceSensor()`.
    fn replace_proximity_sensor(&mut self, _item_id: i64) {}

    /// `try_add_behavior_context_tags(proximitySensor, context)`.
    fn add_behavior_context_tags(&mut self, _item_id: i64) {}

    /// `proximitySensor.AddTag(tag)`.
    fn add_proximity_sensor_tag(&mut self, _item_id: i64, _tag: &str) {}

    /// `proximitySensor.RegisterSensor(ballpark)`: the ball id of the sensor.
    fn register_proximity_sensor(&mut self, _item_id: i64) -> Option<i64> {
        None
    }

    /// `ballpark.slims.itervalues()`: every slim item of the ballpark.
    fn slim_items(&self) -> Vec<SlimItem> {
        Vec::new()
    }

    /// `ballpark.HasPendingCloak(ballId)`.
    fn has_pending_cloak(&self, _ball_id: i64) -> bool {
        false
    }

    /// `ballpark.IsBallProtected(ballId)`.
    fn is_ball_protected(&self, _ball_id: i64) -> bool {
        false
    }

    /// `ballpark.GetBallIdsAndDistInRange(ballId, range)`: `(distance squared, ball id)`.
    fn ball_ids_and_dist_in_range(&self, _ball_id: i64, _range: f64) -> Vec<(f64, i64)> {
        Vec::new()
    }

    /// `ball.sourceID`: who launched a ball (a drone's, a missile's).
    fn ball_source_id(&self, _ball_id: i64) -> Option<i64> {
        None
    }
    /// `myBall.ApproachObject(targetId, approachRange=, cruise=)`.
    fn approach_object(&mut self, _ball_id: i64, _target_id: i64, _approach_range: f64, _cruise: bool) {}

    /// `ballpark.SetTargetTracking(itemId, targetId, notifyRange)`.
    fn set_target_tracking(&mut self, _item_id: i64, _target_id: i64, _notify_range: f64) {}

    /// What the ball calls (`myBall.DoTargetTracking = ...`, `myBall.DoCollision = ...`): the observers of the task that wants them, or none when the
    /// task put a function that does nothing there.
    fn set_ball_callbacks(
        &mut self,
        _ball_id: i64,
        _do_target_tracking: Option<crate::blackboards::Observer>,
        _do_collision: Option<crate::blackboards::Observer>,
    ) {
    }

    /// `ballpark.StopEx(itemId)`.
    fn stop_ex(&mut self, _item_id: i64) {}

    /// `ballpark.ExplodeEx(ballId)`.
    fn explode_ex(&mut self, _ball_id: i64) {}

    /// `ballpark.MissileFollow(fighterId, targetId, controllerId)`.
    fn missile_follow(&mut self, _fighter_id: i64, _target_id: i64, _controller_id: i64) {}

    /// `ballpark.SetBallMassive(ballId, massive)`.
    fn set_ball_massive(&mut self, _ball_id: i64, _massive: bool) {}

    /// `GetControlledFighterRegistryForSolarsystem(solarsystemId).GetControllerForFighter(fighterId)`.
    fn controller_for_fighter(&self, _fighter_id: i64) -> Option<i64> {
        None
    }
    /// `ballpark.GetShipsInSameBubble(itemId)`.
    fn ships_in_same_bubble(&self, _item_id: i64) -> Vec<i64> {
        Vec::new()
    }

    /// `ballpark.specialEffects.StartShipEffect(shipId, effectName, duration, repeat, graphicInfo=)`.
    fn start_ship_effect(
        &mut self,
        _ship_id: i64,
        _effect_name: &str,
        _duration: &Value,
        _repeat: i64,
        _graphic_info: Option<&Value>,
    ) {
    }

    /// `ballpark.CancelIfDoingStuff(shipId)`: true when the ship is still busy.
    fn cancel_if_doing_stuff(&mut self, _ship_id: i64) -> bool {
        false
    }

    /// `ballpark.WarpTo(shipId, destination, minRange=, cheatWarp=False, warpSpeed=, jitterRadius=)`.
    fn warp_to(
        &mut self,
        _ship_id: i64,
        _destination: [f64; 3],
        _min_range: f64,
        _warp_speed: f64,
        _jitter_radius: f64,
    ) {
    }

    /// `ball.NPCWarpTo(destination, jitterRadius=, warpAtRange=, warpSpeed=)`: nothing for a ball that has no such method, and what the method
    /// raises otherwise.
    fn npc_warp_to(
        &mut self,
        _ball_id: i64,
        _destination: [f64; 3],
        _jitter_radius: f64,
        _warp_at_range: f64,
        _warp_speed: f64,
    ) -> Option<WorldResult<()>> {
        None
    }

    /// `ballpark.GotoPoint(ballId, x, y, z)`.
    fn goto_point(&mut self, _ball_id: i64, _position: [f64; 3]) -> WorldResult<()> {
        Ok(())
    }

    /// `ballpark.SetSpeedFraction(ballId, fraction)`.
    fn set_speed_fraction(&mut self, _ball_id: i64, _fraction: f64) -> WorldResult<()> {
        Ok(())
    }

    /// `FormationPicker(ballpark, first ship, None, destination, candidates, 0.0, None, settings, applySkills=False)` and what it answers: where each
    /// ship goes and how the warp is done.
    fn pick_formation(
        &mut self,
        _candidates: &[(i64, Option<i64>)],
        _destination_coordinates: &Value,
        _settings: &FormationSettings,
    ) -> Formation {
        Formation::default()
    }
    /// `ballpark.specialEffects.StartStretchEffect(sourceId, targetId, effectName, duration, repeat, moduleId, graphicInfo=)`.
    #[allow(clippy::too_many_arguments)]
    fn start_stretch_effect(
        &mut self,
        _source_id: i64,
        _target_id: i64,
        _effect_name: &str,
        _duration: &Value,
        _repeat: &Value,
        _module_id: Option<i64>,
        _graphic_info: Option<&Value>,
    ) {
    }

    /// `ballpark.specialEffects.StopStretchEffect(sourceId, targetId, effectName, moduleId)`.
    fn stop_stretch_effect(
        &mut self,
        _source_id: i64,
        _target_id: i64,
        _effect_name: &str,
        _module_id: Option<i64>,
    ) {
    }
    /// `ballpark.UpdateSlimItemField(itemId, name, value)`.
    fn update_slim_item_field(&mut self, _item_id: i64, _name: &str, _value: Value) {}

    /// `ballpark.UpdateSlimItemFieldMulti(itemId, **fields)`.
    fn update_slim_item_field_multi(&mut self, _item_id: i64, _fields: &[(&str, Value)]) {}

    /// `ballpark.GetWarpDestinationPoint(shipId, targetId)`: where a warp to the target ends.
    fn warp_destination_point(&self, _ship_id: i64, _target_id: i64) -> [f64; 3] {
        [0.0; 3]
    }

    /// `ballpark.ChangeBallPosition(ballId, x, y, z)`.
    fn change_ball_position(&mut self, _ball_id: i64, _position: [f64; 3]) {}

    /// `ballpark.RemoveItemFromPark(item)`.
    fn remove_item_from_park(&mut self, _item_id: i64) {}

    /// `ballpark.sunID`.
    fn sun_id(&self) -> Option<i64> {
        None
    }

    /// `ball.NPCEndWarp()`.
    fn npc_end_warp(&mut self, _ball_id: i64) -> WorldResult<()> {
        Ok(())
    }

    /// `ballpark.CheckBall(ballId)`: what it raises for a ball that is not in the park.
    fn check_ball(&self, _ball_id: i64) -> WorldResult<()> {
        Ok(())
    }

    /// `ball.broker is None`: the ball was released.
    fn is_ball_released(&self, _ball_id: i64) -> bool {
        false
    }

    /// `ball.GotoPoint(position, notificationRange=, callback=(observer, ()))`: the observer is told (`entered`) when the ball is in range.
    fn goto_point_notify(
        &mut self,
        _ball_id: i64,
        _position: [f64; 3],
        _notification_range: f64,
        _observer: crate::blackboards::Observer,
    ) {
    }

    /// `ball.ApproachObject(targetId, notificationRange=, approachRange=, callback=(observer, ()))`.
    fn approach_object_notify(
        &mut self,
        _ball_id: i64,
        _target_id: i64,
        _notification_range: f64,
        _approach_range: f64,
        _observer: crate::blackboards::Observer,
    ) {
    }

    /// `ball.OrbitObject(targetId, approachRange=, notificationRange=, callback=(observer, ()))`; what it raises (`UserError`).
    fn orbit_object(
        &mut self,
        _ball_id: i64,
        _target_id: i64,
        _approach_range: f64,
        _notify: Option<(f64, crate::blackboards::Observer)>,
    ) -> WorldResult<()> {
        Ok(())
    }

    /// `ball.StopMovement()`.
    fn stop_movement(&mut self, _ball_id: i64) {}

    /// `ball.StopMovementEvents()`.
    fn stop_movement_events(&mut self, _ball_id: i64) {}

    /// `ball.SetMovementSpeed(cruise=)`.
    fn set_movement_speed(&mut self, _ball_id: i64, _cruise: bool) {}

    /// `ballpark.GotoDirection(ballId, x, y, z)`.
    fn goto_direction(&mut self, _ball_id: i64, _direction: [f64; 3]) -> WorldResult<()> {
        Ok(())
    }

    /// `ball.GetEntityVelocity().set_custom_orbit_velocity(velocity)`.
    fn set_custom_orbit_velocity(&mut self, _ball_id: i64, _velocity: Option<f64>) {}

    /// `ball.GetEntityVelocity().remove_custom_orbit_velocity()`.
    fn remove_custom_orbit_velocity(&mut self, _ball_id: i64) {}

    /// `getattr(slimItem, name, None)`: a field of the slim item that the tasks name (`sourceShipID`, whatever a behavior stores).
    fn slim_item_attribute(&self, _item_id: i64, _name: &str) -> Value {
        Value::None
    }

    /// `componentRegistry.GetInstancesWithComponentClass(NPC_WARP_BEACON)`: the items.
    fn npc_warp_beacon_item_ids(&self) -> Vec<i64> {
        Vec::new()
    }

    /// `HasStoreSlimItemFieldInItemSettingsComponent(typeId)`.
    fn has_store_slim_item_field_component(&self, _type_id: i64) -> bool {
        false
    }

    /// `StoreSlimItemFieldInItemSettings.GetPersistentSlimItemAttributesForType(typeId)`.
    fn persistent_slim_item_attributes_for_type(&self, _type_id: i64) -> Vec<String> {
        Vec::new()
    }

    /// `componentRegistry.GetComponentForItem(itemId, STORE_SLIM_ITEM_FIELD_IN_ITEM_SETTINGS).SaveSlimItemSetting(ballpark, field, value)`.
    fn save_slim_item_setting(&mut self, _item_id: i64, _field: &str, _value: &Value) {}

    /// `GetRandomPlacementNearRandomPlanet(solarsystemId, minDistance, maxDistance)` (`ballpark.locationgenerator`).
    fn random_placement_near_random_planet(
        &mut self,
        _solar_system_id: Option<i64>,
        _min_distance: f64,
        _max_distance: f64,
    ) -> Option<[f64; 3]> {
        None
    }

    /// `cfg.evelocations.Get(locationId)`: where it is.
    fn location_position(&self, _location_id: i64) -> Option<[f64; 3]> {
        None
    }

    /// `ballpark.GetItemsInBubble(None, itemID=ballId)`: the balls of the bubble the ball is in.
    fn items_in_bubble_of(&self, ball_id: i64) -> Vec<i64> {
        self.ball(ball_id).and_then(|ball| self.ball_ids_in_bubble(ball.new_bubble_id)).unwrap_or_default()
    }

    /// `GetWarpBubbleItemIdsWithinRangeOfBall(ballpark, ballId)`: `(warp disruption probes, ships with a bubble)` that reach the ball.
    fn warp_bubble_item_ids_within_range_of_ball(&self, ball_id: i64) -> (Vec<i64>, Vec<i64>) {
        match self.ball(ball_id) {
            Some(ball) => self.warp_bubble_item_ids_within_range_of_coordinate(ball.position),
            None => (Vec::new(), Vec::new()),
        }
    }

    /// `ballpark.InSameBubble(itemId, otherId)`.
    fn in_same_bubble(&self, item_id: i64, other_id: i64) -> bool {
        match (self.ball(item_id), self.ball(other_id)) {
            (Some(ball), Some(other)) => ball.new_bubble_id == other.new_bubble_id,
            _ => false,
        }
    }
}
