//! `SceneWorld` as the ballpark: the balls of the scene (its ships and the bodies of the system), where they are and how they move, and the
//! orders that move them. A bubble is a box of the public grid (`crate::visibility`): the reference groups balls in bubbles too, but its bubbles are
//! not part of the client, and the box is what the scene already uses to tell who sees whom.

use eve_behaviors::consts::{
    DESTINY_MODE_FOLLOW, DESTINY_MODE_GOTO, DESTINY_MODE_ORBIT, DESTINY_MODE_STOP, DESTINY_MODE_WARP,
    SHIP_NOT_WARPING,
};
use eve_behaviors::env::{Ball, Ballpark, SlimItem, WorldError, WorldResult};
use eve_destiny::Vec3;

use super::world::{SceneWorld, distance};
use super::{NotifyTarget, RangeNotification};
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
            Course::Goto => (DESTINY_MODE_GOTO, 0),
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

impl Ballpark for SceneWorld<'_> {
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
        self.all_ball_ids()
            .into_iter()
            .filter(|id| *id != ball_id)
            .filter_map(|id| {
                let squared = distance(from, self.ball_position(id)?).powi(2);
                (squared <= range * range).then_some((squared, id))
            })
            .collect()
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
