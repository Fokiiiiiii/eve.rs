//! The server's `ballpark` as the tasks see it: balls and their slim items, bubbles, the state of a ship in space, and what a task can ask a ball
//! to do. The server's ballpark is not part of the client; only what the tasks use of it is here, with the names of the client's methods in snake
//! case. What the client's calls raise is `WorldError`.

use super::{WorldError, WorldResult};

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
}
