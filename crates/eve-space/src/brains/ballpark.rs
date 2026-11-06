//! `SceneWorld` as the ballpark: the balls of the scene (its ships and the bodies of the system), where they are and how they move, and the
//! orders that move them. A bubble is a box of the public grid (`crate::visibility`): the reference groups balls in bubbles too, but its bubbles are
//! not part of the client, and the box is what the scene already uses to tell who sees whom.
use eve_behaviors::consts::{
    DESTINY_MODE_FOLLOW, DESTINY_MODE_GOTO, DESTINY_MODE_ORBIT, DESTINY_MODE_STOP, DESTINY_MODE_WARP,
    SHIP_NOT_WARPING,
};
use eve_behaviors::env::{Ball, Ballpark, ProximitySensor, SlimItem, WorldError, WorldResult};
use eve_behaviors::value::Value as TaskValue;
use eve_destiny::Vec3;

use super::world::{SceneWorld, distance};
use std::collections::BTreeSet;

use super::{NotifyTarget, RangeNotification, Sensor};
use crate::link::SlimValue;
use crate::npc::is_npc_session;
use crate::scene::Command;
use crate::ship::Course;
use crate::visibility::grid_of;
use crate::world::BodyKind;

/// The id of the bubble a place is in: the box of the grid, packed (never 0 or negative: -1 is the ballpark's "no bubble").
pub(crate) fn bubble_of(position: [f64; 3]) -> i64 {
    let (x, y, z) = grid_of(Vec3::new(position[0], position[1], position[2]));
    const OFFSET: i64 = 1 << 20;
    let pack = |axis: i64| (axis + OFFSET).clamp(0, (1 << 21) - 1);
    (pack(x) << 42) | (pack(y) << 21) | pack(z) | (1 << 62)
}

fn as_array(v: Vec3) -> [f64; 3] {
    [v.x, v.y, v.z]
}

/// A task's value as a dynamic slim-item field value: none, int, float, bool, text, and a list or tuple of those. A set or a dict has no slim form
/// and is not sent.
fn slim_value_of(value: &TaskValue) -> Option<SlimValue> {
    Some(match value {
        TaskValue::None => SlimValue::None,
        TaskValue::Int(number) => SlimValue::Int(*number),
        TaskValue::Float(number) => SlimValue::Real(*number),
        TaskValue::Bool(flag) => SlimValue::Bool(*flag),
        TaskValue::Str(text) => SlimValue::Text(text.clone()),
        TaskValue::Tuple(items) | TaskValue::List(items) => {
            SlimValue::Tuple(items.iter().map(slim_value_of).collect::<Option<Vec<_>>>()?)
        }
        TaskValue::Set(_) | TaskValue::Dict(_) => return None,
    })
}

impl SceneWorld<'_> {
    /// Where a ball of the scene is.
    pub(crate) fn ball_position(&self, ball_id: i64) -> Option<[f64; 3]> {
        if let Some(session) = self.session_of(ball_id) {
            return self.scene.pilots.get(&session).map(|pilot| as_array(pilot.ship.position));
        }
        self.scene.system().body(ball_id).map(|body| as_array(body.position))
    }

    fn ball_radius(&self, ball_id: i64) -> Option<f64> {
        if let Some(session) = self.session_of(ball_id) {
            return self.scene.pilots.get(&session).map(|pilot| pilot.ship.spec.radius);
        }
        self.scene.system().body(ball_id).map(|body| body.radius)
    }

    /// The distance between the surfaces of two balls.
    pub(crate) fn surface_distance(&self, a: i64, b: i64) -> Option<f64> {
        let (pa, pb) = (self.ball_position(a)?, self.ball_position(b)?);
        Some(distance(pa, pb) - self.ball_radius(a)? - self.ball_radius(b)?)
    }

    pub(crate) fn ships_in_sensor_range(&self, item_id: i64) -> BTreeSet<i64> {
        let Some(sensor) = self.state.sensors.get(&item_id) else { return BTreeSet::new() };
        let Some(from) = self.ball_position(item_id) else { return BTreeSet::new() };
        let candidates = self.scene.in_entry_order_ships().into_iter().filter_map(|ship| {
            Some(crate::geo::SpatialEntity {
                id: ship,
                kind: crate::geo::SpatialKind::Ship,
                character_id: None,
                owner_id: None,
                corporation_id: None,
                alliance_id: None,
                type_id: None,
                group_id: None,
                category_id: None,
                position: self.ball_position(ship)?,
                radius: self.ball_radius(ship).unwrap_or(0.0),
                warping: false,
                item: ship,
            })
        });
        crate::geo::SpatialQuery::radius(
            from,
            sensor.detection_range,
            crate::geo::SpatialFilter { exclude_id: Some(item_id), ..crate::geo::SpatialFilter::default() },
        )
        .execute(candidates)
        .into_iter()
        .map(|hit| hit.item)
        .collect()
    }

    fn sensor_of(&self, item_id: i64) -> Option<ProximitySensor> {
        self.state.sensors.get(&item_id)?;
        let position = self.ball_position(item_id)?;
        Some(ProximitySensor {
            item_id,
            bubble_id: bubble_of(position),
            ball_ids_in_range: self.ships_in_sensor_range(item_id).into_iter().collect(),
        })
    }

    /// The ids of every ball of the scene: the ships, then the bodies.
    fn all_ball_ids(&self) -> Vec<i64> {
        let mut ids: Vec<i64> = self.scene.in_entry_order_ships().into_iter().collect();
        ids.extend(self.scene.system().bodies.iter().map(|body| body.id));
        ids
    }

    fn ship_ball(&self, ball_id: i64) -> Option<Ball> {
        let session = self.session_of(ball_id)?;
        let ship = &self.scene.pilots.get(&session)?.ship;
        let (mode, follow_id) = match ship.course {
            Course::Goto | Course::GotoPoint { .. } => (DESTINY_MODE_GOTO, 0),
            Course::Stop => (DESTINY_MODE_STOP, 0),
            Course::Follow { target, .. } => (DESTINY_MODE_FOLLOW, target),
            Course::Orbit { target, .. } => (DESTINY_MODE_ORBIT, target),
            Course::Warp { .. } => (DESTINY_MODE_WARP, 0),
        };
        let position = as_array(ship.position);
        let heading = as_array(ship.direction);
        Some(Ball {
            id: ball_id,
            position,
            velocity: as_array(ship.velocity),
            goto: [
                position[0] + heading[0] * 1.0e16,
                position[1] + heading[1] * 1.0e16,
                position[2] + heading[2] * 1.0e16,
            ],
            mode,
            new_bubble_id: bubble_of(position),
            follow_id,
            radius: ship.spec.radius,
            is_cloaked: false,
            is_free: true,
            mini_balls: Vec::new(),
        })
    }
}

impl SceneWorld<'_> {
    /// `UpdateSlimItemField`: the field is set on the scene's deployable, and those who see it are told its slim item again.
    /// shortcut: a ship or a body keeps no dynamic slim fields yet (the call is a no-op for it); the scene's ship and body entries have no store to read
    /// them from. Upgrade when a behaviour sets one on a ship (fwAttackMethod on NPC ships, DEVIATIONS "Slim item fields").
    fn set_slim_field(&mut self, item_id: i64, name: &str, value: &TaskValue) {
        let Some(value) = slim_value_of(value) else { return };
        let told = self.scene.set_slim_field(item_id, name.to_owned(), value, self.now);
        self.out.extend(told.into_iter().filter(|out| !is_npc_session(out.session())));
    }
}

impl Ballpark for SceneWorld<'_> {
    fn update_slim_item_field(&mut self, item_id: i64, name: &str, value: TaskValue) {
        self.set_slim_field(item_id, name, &value);
    }

    fn update_slim_item_field_multi(&mut self, item_id: i64, fields: &[(&str, TaskValue)]) {
        for (name, value) in fields {
            self.set_slim_field(item_id, name, value);
        }
    }

    fn has_ball(&self, ball_id: i64) -> bool {
        self.session_of(ball_id).is_some() || self.scene.system().body(ball_id).is_some()
    }

    fn ball(&self, ball_id: i64) -> Option<Ball> {
        if let Some(ball) = self.ship_ball(ball_id) {
            return Some(ball);
        }
        let body = self.scene.system().body(ball_id)?;
        let position = as_array(body.position);
        Some(Ball {
            id: ball_id,
            position,
            velocity: [0.0; 3],
            goto: position,
            mode: DESTINY_MODE_STOP,
            new_bubble_id: bubble_of(position),
            follow_id: 0,
            radius: body.radius,
            is_cloaked: false,
            is_free: false,
            mini_balls: Vec::new(),
        })
    }

    fn slim_item(&self, item_id: i64) -> Option<SlimItem> {
        if let Some(session) = self.session_of(item_id) {
            let spec = &self.scene.pilots.get(&session)?.ship.spec;
            return Some(SlimItem {
                item_id,
                type_id: i64::from(spec.type_id),
                group_id: i64::from(spec.group_id),
                category_id: i64::from(spec.category_id),
                owner_id: Some(spec.owner_id),
                char_id: (spec.character_id > 0).then_some(spec.character_id),
            });
        }
        let body = self.scene.system().body(item_id)?;
        Some(SlimItem {
            item_id,
            type_id: i64::from(body.type_id),
            group_id: i64::from(body.group_id),
            category_id: i64::from(body.category_id),
            owner_id: Some(body.owner_id),
            char_id: None,
        })
    }

    fn ball_ids_in_bubble(&self, bubble_id: i64) -> Option<Vec<i64>> {
        let ids: Vec<i64> = self
            .all_ball_ids()
            .into_iter()
            .filter(|id| self.ball_position(*id).is_some_and(|p| bubble_of(p) == bubble_id))
            .collect();
        (!ids.is_empty()).then_some(ids)
    }

    fn ship_ids_in_bubble(&self, bubble_id: i64) -> Vec<i64> {
        self.scene
            .in_entry_order_ships()
            .into_iter()
            .filter(|id| self.ball_position(*id).is_some_and(|p| bubble_of(p) == bubble_id))
            .collect()
    }

    fn global_ball_ids(&self) -> Vec<i64> {
        self.scene.system().bodies.iter().map(|body| body.id).collect()
    }

    fn surface_dist(&self, a: i64, b: i64) -> WorldResult<f64> {
        self.surface_distance(a, b).ok_or_else(|| WorldError::Runtime("no such ball".to_string()))
    }

    fn distance_between(&self, a: i64, b: i64) -> WorldResult<f64> {
        let (pa, pb) = (self.ball_position(a), self.ball_position(b));
        match (pa, pb) {
            (Some(pa), Some(pb)) => Ok(distance(pa, pb)),
            _ => Err(WorldError::Runtime("no such ball".to_string())),
        }
    }

    fn is_warping(&self, ball_id: i64) -> WorldResult<i64> {
        match self.ship_ball(ball_id) {
            Some(ball) if ball.mode == DESTINY_MODE_WARP => Ok(1),
            _ => Ok(SHIP_NOT_WARPING),
        }
    }

    fn bubble_at_coordinates(&self, position: [f64; 3]) -> i64 {
        bubble_of(position)
    }

    fn solar_system_id(&self) -> Option<i64> {
        Some(i64::from(self.scene.system().id))
    }

    fn ball_ids_and_dist_in_range(&self, ball_id: i64, range: f64) -> Vec<(f64, i64)> {
        let Some(from) = self.ball_position(ball_id) else { return Vec::new() };
        let candidates = self.all_ball_ids().into_iter().enumerate().filter_map(|(order, id)| {
            let order = order as u64;
            let position = self.ball_position(id)?;
            if let Some(session) = self.session_of(id)
                && let Some(pilot) = self.scene.pilots.get(&session)
            {
                let mut entity = self.scene.spatial_ship(session, pilot);
                entity.position = position;
                entity.item = order;
                return Some(entity);
            }
            let (kind, radius) = match self.scene.system().body(id) {
                Some(body) => (crate::geo::SpatialKind::SystemBody, body.radius),
                None => (crate::geo::SpatialKind::Ship, self.ball_radius(id).unwrap_or(0.0)),
            };
            Some(crate::geo::SpatialEntity {
                id,
                kind,
                character_id: None,
                owner_id: None,
                corporation_id: None,
                alliance_id: None,
                type_id: None,
                group_id: None,
                category_id: None,
                position,
                radius,
                warping: false,
                item: order,
            })
        });
        let mut hits = crate::geo::SpatialQuery::radius(
            from,
            range,
            crate::geo::SpatialFilter { exclude_id: Some(ball_id), ..crate::geo::SpatialFilter::default() },
        )
        .execute(candidates);
        hits.sort_by_key(|hit| hit.item);
        hits.into_iter().map(|hit| (hit.distance.powi(2), hit.id)).collect()
    }

    fn ships_in_same_bubble(&self, item_id: i64) -> Vec<i64> {
        match self.ball_position(item_id) {
            Some(position) => self.ship_ids_in_bubble(bubble_of(position)),
            None => Vec::new(),
        }
    }

    fn in_same_bubble(&self, item_id: i64, other_id: i64) -> bool {
        match (self.ball_position(item_id), self.ball_position(other_id)) {
            (Some(a), Some(b)) => bubble_of(a) == bubble_of(b),
            _ => false,
        }
    }

    fn items_in_bubble_of(&self, ball_id: i64) -> Vec<i64> {
        match self.ball_position(ball_id) {
            Some(position) => self.ball_ids_in_bubble(bubble_of(position)).unwrap_or_default(),
            None => Vec::new(),
        }
    }

    fn sun_id(&self) -> Option<i64> {
        self.scene.system().bodies.iter().find(|body| body.kind == BodyKind::Star).map(|body| body.id)
    }

    fn check_ball(&self, ball_id: i64) -> WorldResult<()> {
        if self.has_ball(ball_id) { Ok(()) } else { Err(WorldError::Key(ball_id.to_string())) }
    }

    fn approach_object(&mut self, ball_id: i64, target_id: i64, approach_range: f64, _cruise: bool) {
        self.command(ball_id, Command::FollowBall { target_id, range: approach_range });
    }

    fn approach_object_notify(
        &mut self,
        ball_id: i64,
        target_id: i64,
        notification_range: f64,
        approach_range: f64,
        observer: eve_behaviors::blackboards::Observer,
    ) {
        self.command(ball_id, Command::FollowBall { target_id, range: approach_range });
        self.notify_when_in_range(RangeNotification {
            ball: ball_id,
            target: NotifyTarget::Ball(target_id),
            range: notification_range,
            observer,
        });
    }

    fn goto_point(&mut self, ball_id: i64, position: [f64; 3]) -> WorldResult<()> {
        let Some(from) = self.ball_position(ball_id) else {
            return Err(WorldError::Key(ball_id.to_string()));
        };
        let heading = Vec3::new(position[0] - from[0], position[1] - from[1], position[2] - from[2]);
        self.command(ball_id, Command::GotoDirection(heading));
        Ok(())
    }

    fn goto_point_notify(
        &mut self,
        ball_id: i64,
        position: [f64; 3],
        notification_range: f64,
        observer: eve_behaviors::blackboards::Observer,
    ) {
        if self.goto_point(ball_id, position).is_ok() {
            self.notify_when_in_range(RangeNotification {
                ball: ball_id,
                target: NotifyTarget::Place(position),
                range: notification_range,
                observer,
            });
        }
    }

    fn orbit_object(
        &mut self,
        ball_id: i64,
        target_id: i64,
        approach_range: f64,
        notify: Option<(f64, eve_behaviors::blackboards::Observer)>,
    ) -> WorldResult<()> {
        if !self.has_ball(target_id) {
            return Err(WorldError::User("TargetNotFound".to_string()));
        }
        self.command(ball_id, Command::Orbit { target_id, distance: approach_range });
        if let Some((range, observer)) = notify {
            self.notify_when_in_range(RangeNotification {
                ball: ball_id,
                target: NotifyTarget::Ball(target_id),
                range,
                observer,
            });
        }
        Ok(())
    }

    fn proximity_sensor(&self, item_id: i64) -> Option<ProximitySensor> {
        self.sensor_of(item_id)
    }

    fn proximity_sensors(&self, tags: &[String]) -> Vec<ProximitySensor> {
        self.state
            .sensors
            .iter()
            .filter(|(_, sensor)| tags.iter().all(|tag| sensor.tags.contains(tag)))
            .filter_map(|(item, _)| self.sensor_of(*item))
            .collect()
    }

    fn add_proximity_sensor(
        &mut self,
        item_id: i64,
        type_id: i64,
        detection_range: f64,
    ) -> Option<ProximitySensor> {
        self.state.sensors.insert(
            item_id,
            Sensor { type_id, detection_range, tags: Vec::new(), in_range: BTreeSet::new() },
        );
        self.sensor_of(item_id)
    }

    fn add_proximity_sensor_tag(&mut self, item_id: i64, tag: &str) {
        if let Some(sensor) = self.state.sensors.get_mut(&item_id)
            && !sensor.tags.iter().any(|t| t == tag)
        {
            sensor.tags.push(tag.to_string());
        }
    }

    fn register_proximity_sensor(&mut self, item_id: i64) -> Option<i64> {
        self.state.sensors.contains_key(&item_id).then_some(item_id)
    }

    /// The sensor starts seeing afresh: what it saw is seen again.
    fn replace_proximity_sensor(&mut self, item_id: i64) {
        if let Some(sensor) = self.state.sensors.get_mut(&item_id) {
            sensor.in_range.clear();
        }
    }

    fn stop_movement(&mut self, ball_id: i64) {
        self.command(ball_id, Command::Stop);
    }

    fn stop_ex(&mut self, item_id: i64) {
        self.command(item_id, Command::Stop);
    }

    fn set_speed_fraction(&mut self, ball_id: i64, fraction: f64) -> WorldResult<()> {
        self.command(ball_id, Command::SetSpeedFraction(fraction));
        Ok(())
    }

    fn goto_direction(&mut self, ball_id: i64, direction: [f64; 3]) -> WorldResult<()> {
        self.command(ball_id, Command::GotoDirection(Vec3::new(direction[0], direction[1], direction[2])));
        Ok(())
    }
}
