//! Things a module puts in space for a while, kept apart from the ships like a missile (`missile_entity`): an interdiction probe, which is a warp-disruption source
//! for its lifetime, and a bomb, which is released at its ship and detonates where it is when its lifetime is up. The client is told each as a ball
//! (`AddBalls2` when it is released, `RemoveBalls` when it is gone, the bomb's explosion played first). Both lifetimes are the simulation's own clock (the SDE's
//! explosion delay): nothing is persisted, so a restart drops them (ADR 0012 addendum, a stated limitation).
//!

//! the recipients are the ships of the scene within that surface distance, but the ship that launched it (as a smartbomb spares its own).

use std::collections::{BTreeMap, BTreeSet};

use eve_destiny::ball::LegacyMode;
use eve_destiny::{BallEntity, IdRef, Kind, Vec3};
use eve_marshal::Value;

use crate::health::Damage;
use crate::link::{AnalysisBeaconState, SlimValue};
use crate::missile_entity::CHARGE_CATEGORY;
use crate::modules::Effect;
use crate::npc::is_npc_session;
use crate::payload;
use crate::scene::{Command, Out, Scene, SessionId, TICKS_PER_SECOND};
use crate::visibility::{Presence, can_see};
use crate::wire::{filetime, id, int, list, real, text, tuple, word};

/// The id of the first deployable a scene makes (its own range, apart from the missiles and the wrecks).
pub const DEPLOYABLE_ID_BASE: i64 = 4_250_000_000_000_000;

/// The charge a deployable is: its type, its group, what the client calls it and the radius of its ball.
#[derive(Debug, Clone, PartialEq)]
pub struct Spec {
    pub charge_type_id: i32,
    pub group_id: i32,
    pub name: String,
    pub radius: f64,
}

/// What a bomb does where it goes off: the damage of a damage bomb, the ECM strength of a jamming bomb, or the capacitor it neutralises.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BombKind {
    Damage,
    Ecm,
    Neutralize,
}

/// What a deployable does when its lifetime is up.
#[derive(Debug, Clone, PartialEq)]
pub enum Role {
    /// A warp-disruption source of `range` metres around it, for as long as it lives (an interdiction probe).
    WarpSource { range: f64 },
    /// A bomb: at its end the ships within `blast_radius` take its kind of effect (`ecm` for the jam, `neutralize` for the capacitor, `damage` for the hit).
    Bomb { kind: BombKind, blast_radius: f64, damage: Damage, ecm: [f64; 4], neutralize: f64 },
    /// A cynosural field (the field types of the generators, group 2020 or 897): it lives until its lifetime is up, and does nothing else. Lit where its ship is.
    CynoField,
    /// A scanner probe the host launched (protocol 45): a ball in space that does nothing; the host recalls it or its time is up.
    ScanProbe,
    /// A mobile warp disruptor the host keeps a record of (protocol 48): a warp-disruption source of `range` metres while it is anchored (`active`). The host
    /// anchors it when its anchoring delay is over and takes it out when its record's time is up.
    MobileWarpSource { range: f64, active: bool },
    /// A Mobile Cynosural Inhibitor the host keeps a record of (protocol 50): while it is anchored (`active`), no cynosural field lights within `range` metres of it
    /// (EVE.js `findActiveInhibitorConflict`); a covert field is not stopped. The host anchors it and takes it out when its record's time is up.
    CynoInhibitor { active: bool, range: f64 },
    /// A Mobile Cynosural Beacon the host keeps a record of (protocol 58): a cynosural field a pilot may jump to while it is anchored (`active`); it is not lit before
    /// that. The host anchors it when its activation time is over and takes it out when its record's time is up.
    CynoBeacon { active: bool },
    /// A Mobile Observatory the host keeps a record of (protocol 72): while it is anchored (`active`), its pings decloak the cloaked ships of its system (the
    MobileObservatory { active: bool },
    /// A player control tower the host keeps a record of (protocol 73): a ball in space, anchored (`active`) when its anchoring time is over. It has no warp or cyno
    /// effect: the fuel and the modules are not modelled yet.
    ControlTower { active: bool },
    /// A deadspace pocket of a dungeon the host keeps a record of (protocol 74): a region of the scene around its origin, in no client ball. It has no effect in the
    DeadspacePocket,
    /// An acceleration gate of a dungeon room the host keeps a record of (protocol 75): a stargate-kind ball the pilots of its system see, interactive
    /// (EVE.js `abyssalRoomEntity`: `ballInteractive`, group 366). Its transit is the scene's `AccelerationGateTransit` command.
    AccelerationGate,
    /// A battlefield's capture point the host keeps a record of (protocol 77): a ball the pilots of its system see, with its factions and next point time
    /// as its dynamic slim fields. It has no effect in the simulation: the host decides who holds it.
    CapturePoint,
    /// A battlefield's scoreboard the host keeps a record of (protocol 78): a ball the pilots of its system see, whose score is its dynamic slim field.
    /// It has no effect in the simulation.
    BattlefieldScoreboard,
    /// A jump bridge a ship opened (command 28, protocol 54): it lives until its time is up, is put in no client ball, and lets the fleet ships in 2 500 metres of
    /// the ship pass through it to the field (command 31). The field is the one the bridge targets; the destination is that field's system.
    JumpBridge { field_id: i64, destination_system: i32, range_meters: f64 },
    /// A lance's beacon (EVE.js `spawnLanceBeacon`, protocol 39): a transient container the beam is drawn to. It does nothing but exist; it is taken out when
    /// its strike's effect ends (or stops), or when its lifetime is up.
    Beacon,
    /// A CONCORD Rogue Analysis Beacon the host keeps a record of (protocol 61): a ball that does nothing but show what the host says of it (its activation and its
    /// link, which the client reads from its slim item). The host takes it out when its record's time is up.
    AnalysisBeacon { state: AnalysisBeaconState },
}

/// The beacon of a lance (EVE.js `MODULAR_EFFECT_BEACON_TYPE_ID` 41233, group 1704, radius 250): a transient "Modular Effect Beacon" container.
pub const LANCE_BEACON: (i32, i32) = (41_233, 1_704);
pub const LANCE_BEACON_RADIUS: f64 = 250.0;
/// The category of the beacon's container (EVE.js `MODULAR_EFFECT_BEACON_CATEGORY_ID`).
const BEACON_CATEGORY: i32 = 2;
/// The category of a cynosural field (EVE.js `cynosuralField.js`, the category it falls back to: 2).
const CYNO_FIELD_CATEGORY: i32 = 2;
/// The category of a deployable (EVE.js `CATEGORY_DEPLOYABLE`, 22): a mobile warp disruptor's category.
const DEPLOYABLE_CATEGORY: i32 = 22;
/// The category of an acceleration gate's ball (EVE.js's acceleration gate record: type 17831, group 366, category 2).
const ACCELERATION_GATE_CATEGORY: i32 = 2;
/// The category of a capture point's ball (the SDE group 922, "Capture Point", is category 11).
const CAPTURE_POINT_CATEGORY: i32 = 11;
/// The category of a battlefield scoreboard's ball (the SDE group 227 of the scoreboard types is category 2).
const BATTLEFIELD_SCOREBOARD_CATEGORY: i32 = 2;

/// How long a structure's bomb waits for the Host's authorization of its blast before it goes off with no effect (30 seconds of scene time).
const BOMB_IMPACT_TICKS: i64 = 30 * crate::scene::TICKS_PER_SECOND;

/// The correlation a structure's bomb blast is authorized under: the controlled structure, its launcher module, the activation and the lease that
/// launched it, and the cycle. `pending` holds the candidates of the blast while the Host answers.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct BombImpact {
    pub session: SessionId,
    pub structure_id: i64,
    pub module_id: i64,
    pub effect_id: i32,
    pub activation_nonce: u64,
    pub control_lease_generation: i64,
    pub cycle_id: u64,
    pub pending: Option<PendingBombImpact>,
}

/// The candidates of a structure's bomb blast while the Host answers, and when the question was sent.
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct PendingBombImpact {
    pub candidate_ids: Vec<i64>,
    pub sent_at: i64,
}

/// A deployable in space: where it is, when it ends, and who put it there (its launcher, for a bomb's blast and the aggression it makes).
#[derive(Debug, Clone, PartialEq)]
pub struct Deployable {
    pub id: i64,
    pub spec: Spec,
    pub role: Role,
    pub position: Vec3,
    /// When it ends, in the scene's ticks.
    pub ends_at: i64,
    pub launcher: SessionId,
    pub ship_id: i64,
    pub owner_id: i64,
    pub corporation_id: i32,
    pub alliance_id: i32,
    pub module_id: i64,
    pub module_type: i32,
    /// The pilots that were told of it (they are told it is gone).
    pub watchers: BTreeSet<SessionId>,
    /// The dynamic slim-item fields the host set (`SetSlimField`), by name.
    pub slim_fields: BTreeMap<String, SlimValue>,
}

fn ticks(milliseconds: f64) -> i64 {
    (milliseconds * (TICKS_PER_SECOND as f64) / 1000.0).round() as i64
}

fn distance(a: Vec3, b: Vec3) -> f64 {
    ((a.x - b.x).powi(2) + (a.y - b.y).powi(2) + (a.z - b.z).powi(2)).sqrt()
}

/// What the client is told when a deployable is released: its ball (`AddBalls2` with its slim item) at one stamp.
fn release_actions(stamp: u32, deployable: &Deployable) -> Result<Vec<Value>, eve_destiny::BallError> {
    let (kind, mode) = match deployable.role {
        Role::WarpSource { .. } => (Kind::WarpDisruptionProbe, LegacyMode::Stop),
        Role::Bomb { .. } => (Kind::Missile, LegacyMode::Stop),
        Role::Beacon => (Kind::Container, LegacyMode::Stop),
        Role::CynoField => (Kind::Other, LegacyMode::Stop),
        Role::ScanProbe => (Kind::Other, LegacyMode::Stop),
        Role::MobileWarpSource { .. } => (Kind::WarpDisruptionProbe, LegacyMode::Stop),
        Role::CynoInhibitor { .. }
        | Role::CynoBeacon { .. }
        | Role::MobileObservatory { .. }
        | Role::ControlTower { .. }
        | Role::DeadspacePocket => (Kind::Other, LegacyMode::Stop),
        Role::AccelerationGate => (Kind::Stargate, LegacyMode::Stop),
        Role::CapturePoint | Role::BattlefieldScoreboard => (Kind::Other, LegacyMode::Stop),
        Role::JumpBridge { .. } => (Kind::Other, LegacyMode::Stop),
        Role::AnalysisBeacon { .. } => (Kind::Container, LegacyMode::Stop),
    };
    let category = category_of(&deployable.role);
    let ball = BallEntity {
        item_id: IdRef::Valid(deployable.id),
        kind,
        legacy_mode: mode,
        is_massive: Some(true),
        radius: deployable.spec.radius,
        position: deployable.position,
        target_point: Some(deployable.position),
        mass: Some(1.0),
        destiny_force_interactive: (deployable.role == Role::AccelerationGate).then_some(true),
        corporation_id: IdRef::Valid(i64::from(deployable.corporation_id)),
        alliance_id: IdRef::Valid(i64::from(deployable.alliance_id)),
        mode_owner: IdRef::Valid(deployable.ship_id),
        ..BallEntity::default()
    };
    let buffer = payload::add_balls_buffer(stamp, &[ball])?;
    let slim = slim_dict(deployable, category);
    Ok(vec![payload::action(stamp, "AddBalls2", [tuple([buffer, list([slim])])])])
}

/// The slim item of a deployable as the client reads it: the entries of its category, then the dynamic fields the host set.
fn slim_dict(deployable: &Deployable, category: i32) -> Value {
    let mut pairs: Vec<(Value, Value)> =
        slim_entries(deployable, category).into_iter().map(|(k, v)| (word(k), v)).collect();
    pairs.extend(deployable.slim_fields.iter().map(|(name, value)| (text(name.clone()), slim_value(value))));
    Value::Dict(pairs)
}

/// The slim item of a deployable as an object (`foo.SlimItem`), the form `OnSlimItemChange` carries.
fn slim_item(deployable: &Deployable, category: i32) -> Value {
    Value::Object { name: Box::new(word("foo.SlimItem")), args: Box::new(slim_dict(deployable, category)) }
}

/// A dynamic slim-item field's value as the client reads it.
fn slim_value(value: &SlimValue) -> Value {
    match value {
        SlimValue::None => Value::None,
        SlimValue::Int(number) => id(*number),
        SlimValue::Real(number) => real(*number),
        SlimValue::Bool(flag) => Value::Bool(*flag),
        SlimValue::Text(text_value) => text(text_value.clone()),
        SlimValue::Tuple(items) => tuple(items.iter().map(slim_value)),
    }
}

/// The category of a deployable's ball (EVE.js: a mobile deployable is category 22, a beacon and a field keep their own).
fn category_of(role: &Role) -> i32 {
    match role {
        Role::Beacon => BEACON_CATEGORY,
        Role::CynoField => CYNO_FIELD_CATEGORY,
        Role::MobileWarpSource { .. }
        | Role::CynoInhibitor { .. }
        | Role::CynoBeacon { .. }
        | Role::AnalysisBeacon { .. } => DEPLOYABLE_CATEGORY,
        Role::AccelerationGate => ACCELERATION_GATE_CATEGORY,
        Role::CapturePoint => CAPTURE_POINT_CATEGORY,
        Role::BattlefieldScoreboard => BATTLEFIELD_SCOREBOARD_CATEGORY,
        _ => CHARGE_CATEGORY,
    }
}

fn slim_entries(deployable: &Deployable, category: i32) -> Vec<(&'static str, Value)> {
    let mut entries = vec![
        ("itemID", id(deployable.id)),
        ("typeID", int(i64::from(deployable.spec.charge_type_id))),
        ("ownerID", id(deployable.owner_id)),
        ("name", text(deployable.spec.name.clone())),
        ("groupID", int(i64::from(deployable.spec.group_id))),
        ("categoryID", int(i64::from(category))),
        ("sourceShipID", id(deployable.ship_id)),
        ("launchModules", list([id(deployable.module_id)])),
    ];
    if let Role::AnalysisBeacon { state } = &deployable.role {
        entries.extend(analysis_beacon_entries(state));
    }
    entries
}

/// A CRAB's components as its slim item keeps them (EVE.js `buildSlimItem`): `component_activate` (whether it is active, and when it becomes so otherwise; the
/// wait is `activate_comp_durationSeconds`), and `component_linkWithShip` (when it was put in space, the link state, when the link completes, the linked ship).
fn analysis_beacon_entries(state: &AnalysisBeaconState) -> Vec<(&'static str, Value)> {
    let when = |ticks: i64| if ticks == 0 { Value::None } else { filetime(ticks) };
    let linked = if state.linked_ship_id == 0 { Value::None } else { id(state.linked_ship_id) };
    vec![
        (
            "component_activate",
            list([
                Value::Bool(state.active),
                if state.active { Value::None } else { when(state.activate_at) },
            ]),
        ),
        ("activate_comp_durationSeconds", int(i64::from(state.activation_seconds))),
        (
            "component_linkWithShip",
            list([
                when(state.deployed_at),
                int(i64::from(state.link_state)),
                when(state.link_complete_at),
                linked,
            ]),
        ),
    ]
}

impl Scene {
    /// A launcher's cycle starts and releases a deployable at its ship: it is made, put in the scene and shown to those who can see the place. A probe starts
    /// disrupting warps where it is; a bomb is due to go off `lifetime_ms` later.
    pub(crate) fn release_deployable(
        &mut self,
        session: SessionId,
        effect: &Effect,
        spec: Spec,
        role: Role,
        lifetime_ms: f64,
        at: i64,
    ) -> Vec<Out> {
        let is_field = role == Role::CynoField;
        let is_bomb = matches!(role, Role::Bomb { .. });
        let (id, mut out) = self.place_deployable(session, effect, spec, role, None, lifetime_ms, at);
        if is_field {
            out.extend(self.field_lit(id, session, at));
        }
        // a bomb a controlled structure releases goes off only when the Host authorizes its blast; one that cannot name that authority is not let go off
        if is_bomb
            && self.controlled_structure_id(session).is_some()
            && !self.record_bomb_impact(id, session, effect)
        {
            self.deployables.remove(&id);
        }
        out
    }

    /// The ship a bomb is released from: the controlled structure's ship when its launcher controls one, else the ship the bomb was released by.
    fn bomb_own_ship(&self, deployable: &Deployable) -> i64 {
        self.controlled_structure_id(deployable.launcher).unwrap_or(deployable.ship_id)
    }

    /// The correlation a structure's bomb blast is authorized under (the launching cycle of its controlled structure). False when there is none.
    fn record_bomb_impact(&mut self, id: i64, session: SessionId, effect: &Effect) -> bool {
        let Some(structure_id) = self.controlled_structure_id(session) else { return false };
        let Some(control_lease_generation) = self.control_lease_generation(session, structure_id) else {
            return false;
        };
        let Some(run) = self.pilots.get(&session).and_then(|pilot| {
            pilot.modules.running.iter().find(|run| run.effect.module_id == effect.module_id)
        }) else {
            return false;
        };
        self.bomb_impacts.insert(
            id,
            BombImpact {
                session,
                structure_id,
                module_id: effect.module_id,
                effect_id: effect.effect_id,
                activation_nonce: run.activation_nonce,
                control_lease_generation,
                cycle_id: run.cycle_id,
                pending: None,
            },
        );
        true
    }

    /// A lance's beacon is put in space at `offset` from the ship that runs its strike (the strike's `beacon`), for the life of the effect (`lifetime_ms`).
    /// Returns the beacon's id, which the beam's effect is played on, and what the pilots are told.
    pub(crate) fn release_beacon(
        &mut self,
        session: SessionId,
        effect: &Effect,
        offset: [f64; 3],
        lifetime_ms: f64,
        at: i64,
    ) -> (i64, Vec<Out>) {
        let spec = Spec {
            charge_type_id: LANCE_BEACON.0,
            group_id: LANCE_BEACON.1,
            name: "Modular Effect Beacon".to_string(),
            radius: LANCE_BEACON_RADIUS,
        };
        let Some(pilot) = self.pilots.get(&session) else { return (0, Vec::new()) };
        let p = pilot.ship.position;
        let position = eve_destiny::Vec3::new(p.x + offset[0], p.y + offset[1], p.z + offset[2]);
        self.place_deployable(session, effect, spec, Role::Beacon, Some(position), lifetime_ms, at)
    }

    /// The beacon of a module's strike that is in space, or 0 (none, or already taken out).
    pub(crate) fn beacon_of(&self, module_id: i64) -> i64 {
        self.deployables
            .values()
            .find(|deployable| deployable.role == Role::Beacon && deployable.module_id == module_id)
            .map_or(0, |deployable| deployable.id)
    }

    /// A module's beacon is taken out now (its effect has ended): the pilots who were told of it are told it is gone.
    pub(crate) fn take_down_beacon(&mut self, module_id: i64, at: i64) -> Vec<Out> {
        let due: Vec<i64> = self
            .deployables
            .values()
            .filter(|deployable| deployable.role == Role::Beacon && deployable.module_id == module_id)
            .map(|deployable| deployable.id)
            .collect();
        let mut out = Vec::new();
        for deployable_id in due {
            let Some(deployable) = self.deployables.remove(&deployable_id) else { continue };
            let stamp = self.next_stamp(at);
            let actions = vec![payload::remove_ships(stamp, [deployable.id])];
            out.extend(
                deployable
                    .watchers
                    .iter()
                    .filter(|watcher| self.pilots.get(watcher).is_some_and(|pilot| pilot.bound))
                    .map(|watcher| Self::update(*watcher, actions.clone())),
            );
        }
        out
    }

    /// Puts a jump bridge of the ship of `session` in space, where the ship is, for `lifetime_ms` (the module duration). No client ball is made for it: the
    /// reference draws the portal on the ship. Returns false when the ship is not in the scene.
    pub(crate) fn place_bridge(
        &mut self,
        session: SessionId,
        field_id: i64,
        destination_system: i32,
        lifetime_ms: f64,
        range_meters: f64,
        at: i64,
    ) -> bool {
        let Some(pilot) = self.pilots.get(&session) else { return false };
        let bridge = Deployable {
            id: DEPLOYABLE_ID_BASE + self.deployable_sequence as i64,
            spec: Spec { charge_type_id: 0, group_id: 0, name: "Jump Bridge".to_string(), radius: 0.0 },
            role: Role::JumpBridge { field_id, destination_system, range_meters },
            position: pilot.ship.position,
            ends_at: at + ticks(lifetime_ms),
            launcher: session,
            ship_id: pilot.ship.spec.ship_id,
            owner_id: self.combat_owner(session),
            corporation_id: pilot.ship.spec.corporation_id,
            alliance_id: pilot.ship.spec.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.deployable_sequence += 1;
        self.deployables.insert(bridge.id, bridge);
        true
    }

    /// Puts a deployable in space at `position` (its launcher's ship when none is given), and tells those who can see the place of it.
    #[allow(clippy::too_many_arguments)]
    fn place_deployable(
        &mut self,
        session: SessionId,
        effect: &Effect,
        spec: Spec,
        role: Role,
        position: Option<eve_destiny::Vec3>,
        lifetime_ms: f64,
        at: i64,
    ) -> (i64, Vec<Out>) {
        let Some(pilot) = self.pilots.get(&session) else { return (0, Vec::new()) };
        let (position, ship_id) = (position.unwrap_or(pilot.ship.position), pilot.ship.spec.ship_id);
        let (corporation_id, alliance_id) = (pilot.ship.spec.corporation_id, pilot.ship.spec.alliance_id);
        let owner_id = self.combat_owner(session);
        let deployable_id = DEPLOYABLE_ID_BASE + self.deployable_sequence as i64;
        self.deployable_sequence += 1;
        let deployable = Deployable {
            id: deployable_id,
            spec,
            role,
            position,
            ends_at: at + ticks(lifetime_ms),
            launcher: session,
            ship_id,
            owner_id,
            corporation_id,
            alliance_id,
            module_id: effect.module_id,
            module_type: effect.module_type,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        (deployable_id, self.insert_deployable(deployable, at))
    }

    /// Puts a made deployable into the scene: it is kept, and those who can see its place are told of it.
    fn insert_deployable(&mut self, mut deployable: Deployable, at: i64) -> Vec<Out> {
        let watchers = self.missile_audience(deployable.position);
        let stamp = self.next_stamp(at);
        let mut out = Vec::new();
        if let Ok(actions) = release_actions(stamp, &deployable) {
            out.extend(watchers.iter().map(|watcher| Self::update(*watcher, actions.clone())));
        }
        deployable.watchers = watchers.into_iter().collect();
        self.deployables.insert(deployable.id, deployable);
        out
    }

    /// Brings each bound pilot's view into line with the deployables it can see: a pilot that moved into a deployable's box is sent it, and a pilot that moved
    /// out of it is told it is gone (the watchers of a deployable are the pilots that hold it, as the wrecks' and asteroids' views are kept).
    pub(crate) fn reconcile_deployables(&mut self, now: i64) -> Vec<Out> {
        if self.deployables.is_empty() {
            return Vec::new();
        }
        let mut out = Vec::new();
        for observer in self.view_sessions() {
            if is_npc_session(observer) {
                continue;
            }
            let Some(presence) = self.presence(observer) else { continue };
            let mut gone = Vec::new();
            let mut coming = Vec::new();
            for deployable in self.deployables.values() {
                let sees = can_see(presence, Presence { position: deployable.position, warping: false });
                match (sees, deployable.watchers.contains(&observer)) {
                    (true, false) => coming.push(deployable.id),
                    (false, true) => gone.push(deployable.id),
                    _ => {}
                }
            }
            if !gone.is_empty() {
                let stamp = self.next_stamp(now);
                out.push(Self::update(observer, vec![payload::remove_ships(stamp, gone.iter().copied())]));
                for id in gone {
                    if let Some(deployable) = self.deployables.get_mut(&id) {
                        deployable.watchers.remove(&observer);
                    }
                }
            }
            for id in coming {
                let stamp = self.next_stamp(now);
                let Some(actions) =
                    self.deployables.get(&id).and_then(|deployable| release_actions(stamp, deployable).ok())
                else {
                    continue;
                };
                out.push(Self::update(observer, actions));
                if let Some(deployable) = self.deployables.get_mut(&id) {
                    deployable.watchers.insert(observer);
                }
            }
        }
        out
    }

    /// The record of a field that was lit by `session`'s ship at `at`, for the host (`DeployedLit`): its life is what is left of it at `at`, the whole of it.
    fn field_lit(&self, deployable_id: i64, session: SessionId, at: i64) -> Vec<Out> {
        let Some(d) = self.deployables.get(&deployable_id) else { return Vec::new() };
        let lifetime_ms = (d.ends_at - at) as f64 / (TICKS_PER_SECOND as f64 / 1000.0);
        let placement = crate::link::DeployedPlacement {
            solar_system_id: self.solar_system_id(),
            deployable_id: d.id,
            name: d.spec.name.clone(),
            type_id: d.spec.charge_type_id,
            group_id: d.spec.group_id,
            position: [d.position.x, d.position.y, d.position.z],
            radius: d.spec.radius,
            owner_id: d.owner_id,
            corporation_id: d.corporation_id,
            alliance_id: d.alliance_id,
            ship_id: d.ship_id,
            lifetime_ms,
        };
        vec![Out::DeployedLit { session, placement: Box::new(placement) }]
    }

    /// A cynosural field the host keeps a record of is put back in space after a restart: with the id it had, and what is left of its life. It is
    /// given once (a field already in the scene is left as it is) and one with no life left is not put back.
    /// A scanner probe the host launched is put in space where it is (protocol 45). It stays until the host takes it out: its end is the host's, not the
    /// simulation's, so its end time is never reached. Given once: a probe already in space is left as it is.
    pub(crate) fn place_probe(&mut self, placement: crate::link::ProbePlacement, now: i64) -> Vec<Out> {
        if self.deployables.contains_key(&placement.probe_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.probe_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::ScanProbe,
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// The host takes a scanner probe out of space: it is removed and those who see it are told it is gone. Only a probe is taken this way.
    pub(crate) fn remove_probe(&mut self, probe_id: i64, at: i64) -> Vec<Out> {
        if !matches!(self.deployables.get(&probe_id), Some(d) if d.role == Role::ScanProbe) {
            return Vec::new();
        }
        let Some(deployable) = self.deployables.remove(&probe_id) else { return Vec::new() };
        let stamp = self.next_stamp(at);
        let actions = vec![payload::remove_ships(stamp, [deployable.id])];
        deployable
            .watchers
            .iter()
            .filter(|watcher| self.pilots.get(watcher).is_some_and(|pilot| pilot.bound))
            .map(|watcher| Self::update(*watcher, actions.clone()))
            .collect()
    }

    /// A mobile warp disruptor the host keeps a record of is put in space where it lies, anchored or not (protocol 48). Given once: one already in space is
    /// left as it is. Its end is the host's, so it is never reached by the simulation's clock.
    pub(crate) fn place_disruptor(
        &mut self,
        placement: crate::link::DisruptorPlacement,
        now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::MobileWarpSource { range: placement.range, active: placement.active },
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// A Mobile Cynosural Beacon the host keeps a record of is put in space where it lies, anchored or not (protocol 58). Given once; its end is the host's.
    pub(crate) fn place_cyno_beacon(
        &mut self,
        placement: crate::link::DisruptorPlacement,
        now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::CynoBeacon { active: placement.active },
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// A Mobile Observatory the host keeps a record of is put in space where it lies, anchored or not (protocol 72). Given once; its end is the host's.
    pub(crate) fn place_observatory(
        &mut self,
        placement: crate::link::DisruptorPlacement,
        now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::MobileObservatory { active: placement.active },
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// A player control tower the host keeps a record of is put in space where it lies, anchored or not (protocol 73). Given once; its end is the host's.
    pub(crate) fn place_control_tower(
        &mut self,
        placement: crate::link::DisruptorPlacement,
        now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::ControlTower { active: placement.active },
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// A deadspace pocket the host keeps a record of is put in the scene at its origin with its radius (protocol 74). No client ball is made for it (the pocket's
    /// rooms are the client's own), and no pilot is told of it. Given once; its end is the host's.
    pub(crate) fn place_deadspace_pocket(
        &mut self,
        placement: crate::link::DisruptorPlacement,
        _now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let pocket = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::DeadspacePocket,
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: 0,
            owner_id: 0,
            corporation_id: 0,
            alliance_id: 0,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.deployables.insert(pocket.id, pocket);
        Vec::new()
    }

    /// An acceleration gate of a dungeon room is put in space at the point the host gives (protocol 75). Its ball goes to the pilots that see it, as any
    /// deployable's does. Given once; its end is the host's.
    pub(crate) fn place_acceleration_gate(
        &mut self,
        placement: crate::link::DisruptorPlacement,
        now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let gate = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::AccelerationGate,
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: 0,
            owner_id: 0,
            corporation_id: 0,
            alliance_id: 0,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(gate, now)
    }

    /// A capture point of a battlefield is put in space (protocol 77). Its factions (0 is none) and its next point time (0 is none) are its dynamic slim
    /// fields, as the client reads them. Given once; its end is the host's.
    pub(crate) fn place_capture_point(
        &mut self,
        placement: crate::link::CapturePointPlacement,
        now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let faction =
            |value: i32| if value == 0 { SlimValue::None } else { SlimValue::Int(i64::from(value)) };
        let mut slim_fields = BTreeMap::new();
        if placement.attacker_faction != 0 || placement.occupier_faction != 0 {
            let factions = SlimValue::Tuple(vec![
                faction(placement.attacker_faction),
                faction(placement.occupier_faction),
            ]);
            slim_fields.insert("component_fwCapturePoint_factionInfo".to_string(), factions);
        }
        if placement.next_point_time != 0 {
            slim_fields.insert(
                "component_fwCapturePoint_nextPointTime".to_string(),
                SlimValue::Int(placement.next_point_time),
            );
        }
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::CapturePoint,
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: 0,
            owner_id: 0,
            corporation_id: 0,
            alliance_id: 0,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields,
        };
        self.insert_deployable(deployable, now)
    }

    /// A battlefield's scoreboard is put in space (protocol 78). Its score starts at none (the client reads none as 0) and the host sets it. Given once.
    pub(crate) fn place_battlefield_scoreboard(
        &mut self,
        placement: crate::link::ScoreboardPlacement,
        now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::BattlefieldScoreboard,
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: 0,
            owner_id: 0,
            corporation_id: 0,
            alliance_id: 0,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// A Mobile Cynosural Inhibitor the host keeps a record of is put in space where it lies, anchored or not (protocol 50). Given once; its end is the host's.
    pub(crate) fn place_inhibitor(
        &mut self,
        placement: crate::link::DisruptorPlacement,
        now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::CynoInhibitor { active: placement.active, range: placement.range },
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// Whether an anchored cynosural inhibitor covers `position`: one within its range of it, so a field does not light there (a covert field is not asked).
    pub(crate) fn inhibited_at(&self, position: Vec3) -> bool {
        self.deployables
            .values()
            .any(|d| matches!(d.role, Role::CynoInhibitor { active: true, range } if distance(position, d.position) <= range))
    }

    /// The host says a mobile warp disruptor is anchored (its disruption on) or not. A deployable that is not a mobile warp disruptor is left alone.
    /// shortcut: the pilots are not sent a new ball for the change (the client reads the anchored state from the placement only), upgrade when the
    /// reference's presentation update is traced.
    pub(crate) fn set_disruptor_active(&mut self, deployable_id: i64, active: bool) -> Vec<Out> {
        if let Some(Deployable {
            role:
                Role::MobileWarpSource { active: current, .. }
                | Role::CynoInhibitor { active: current, .. }
                | Role::CynoBeacon { active: current }
                | Role::MobileObservatory { active: current }
                | Role::ControlTower { active: current },
            ..
        }) = self.deployables.get_mut(&deployable_id)
        {
            *current = active;
        }
        Vec::new()
    }

    pub(crate) fn restore_field(&mut self, placement: crate::link::DeployedPlacement, now: i64) -> Vec<Out> {
        let lifetime_ms = placement.lifetime_ms;
        if !lifetime_ms.is_finite()
            || lifetime_ms <= 0.0
            || self.deployables.contains_key(&placement.deployable_id)
        {
            return Vec::new();
        }
        if placement.deployable_id >= DEPLOYABLE_ID_BASE {
            let next = (placement.deployable_id - DEPLOYABLE_ID_BASE) as u64 + 1;
            self.deployable_sequence = self.deployable_sequence.max(next);
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::CynoField,
            position: Vec3::new(x, y, z),
            ends_at: now + ticks(lifetime_ms),
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// The host takes a cynosural field or a mobile warp disruptor out of space (its record's time is up, or it was scooped): it is removed and those who see it
    /// are told it is gone. Anything else, or a deployable not in space, is left alone.
    pub(crate) fn remove_field(&mut self, deployable_id: i64, at: i64) -> Vec<Out> {
        if !matches!(self.deployables.get(&deployable_id), Some(d) if matches!(d.role, Role::CynoField | Role::MobileWarpSource { .. } | Role::CynoInhibitor { .. } | Role::CynoBeacon { .. }))
        {
            return Vec::new();
        }
        let Some(deployable) = self.deployables.remove(&deployable_id) else { return Vec::new() };
        let stamp = self.next_stamp(at);
        let actions = vec![payload::remove_ships(stamp, [deployable.id])];
        deployable
            .watchers
            .iter()
            .filter(|watcher| self.pilots.get(watcher).is_some_and(|pilot| pilot.bound))
            .map(|watcher| Self::update(*watcher, actions.clone()))
            .collect()
    }

    /// Remove the named cynosural beacon from this system. The dedicated message carries a beacon ID, so other deployables sharing a broad removal path are not touched.
    pub(crate) fn remove_cyno_beacon(
        &mut self,
        solar_system_id: i32,
        structure_beacon_id: i64,
        at: i64,
    ) -> Vec<Out> {
        if solar_system_id != self.solar_system_id()
            || !matches!(
                self.deployables.get(&structure_beacon_id),
                Some(deployable) if matches!(deployable.role, Role::CynoBeacon { .. })
            )
        {
            return Vec::new();
        }
        self.remove_field(structure_beacon_id, at)
    }

    /// The deployables whose lifetime is up at `now` end, in the order they were released: a probe is taken out of the scene, a bomb goes off where it is
    /// first and is taken out with the explosion played. A structure's bomb goes off only when the Host has authorized its blast (see `await_bomb_impact`).
    pub(crate) fn advance_deployables(&mut self, now: i64) -> Vec<Out> {
        let due: Vec<i64> = self
            .deployables
            .iter()
            .filter(|(_, deployable)| deployable.ends_at <= now)
            .map(|(id, _)| *id)
            .collect();
        let mut out = Vec::new();
        for deployable_id in due {
            if self.bomb_impacts.contains_key(&deployable_id) {
                out.extend(self.await_bomb_impact(deployable_id, now));
                continue;
            }
            let Some(deployable) = self.deployables.remove(&deployable_id) else { continue };
            let explode = matches!(deployable.role, Role::Bomb { .. });
            if explode {
                out.extend(self.detonate(&deployable, now, None));
            }
            out.extend(self.depart(&deployable, explode, now));
        }
        out
    }

    /// A deployable is taken out of the scene: a bomb's explosion is played, and the pilots who were told of it are told it is gone.
    fn depart(&mut self, deployable: &Deployable, explode: bool, now: i64) -> Vec<Out> {
        let stamp = self.next_stamp(now);
        let mut actions = Vec::new();
        if explode {
            actions.push(payload::action(
                stamp,
                "TerminalPlayDestructionEffect",
                [id(deployable.id), int(crate::wreck::DESTRUCTION_EFFECT_EXPLOSION)],
            ));
        }
        actions.push(payload::remove_ships(stamp, [deployable.id]));
        deployable
            .watchers
            .iter()
            .filter(|watcher| self.pilots.get(watcher).is_some_and(|pilot| pilot.bound))
            .map(|watcher| Self::update(*watcher, actions.clone()))
            .collect()
    }

    /// A structure's bomb is due: the Host is asked which of the ships and structures in its blast it may affect (a pulse of the launcher's cycle). A
    /// request that is still unanswered after `BOMB_IMPACT_TICKS` is no grant, and the bomb goes off where it is with no effect on anyone.
    fn await_bomb_impact(&mut self, deployable_id: i64, now: i64) -> Vec<Out> {
        let Some(impact) = self.bomb_impacts.get(&deployable_id).cloned() else { return Vec::new() };
        if let Some(pending) = &impact.pending {
            if now - pending.sent_at < BOMB_IMPACT_TICKS {
                return Vec::new();
            }
            return self.finish_bomb_impact(deployable_id, &BTreeSet::new(), now);
        }
        let Some(deployable) = self.deployables.get(&deployable_id).cloned() else {
            self.bomb_impacts.remove(&deployable_id);
            return Vec::new();
        };
        let Role::Bomb { blast_radius, .. } = &deployable.role else { return Vec::new() };
        let centre = [deployable.position.x, deployable.position.y, deployable.position.z];
        let own_ship = self.bomb_own_ship(&deployable);
        let retained = self
            .controlled_structures
            .get(&deployable.launcher)
            .and_then(|observer| observer.previous_ship_id);
        let mut candidate_ids: Vec<i64> = self
            .ships_in_area(centre, *blast_radius, Some(own_ship), now)
            .into_iter()
            .map(|(_, ship_id)| ship_id)
            .filter(|ship_id| Some(*ship_id) != retained)
            .collect();
        // the launching structure is no target of its own blast
        candidate_ids.extend(
            self.structures_within(centre, *blast_radius)
                .into_iter()
                .filter(|structure| *structure != own_ship),
        );
        candidate_ids.retain(|candidate| *candidate > 0);
        candidate_ids.sort_unstable();
        candidate_ids.dedup();
        if candidate_ids.is_empty() {
            return self.finish_bomb_impact(deployable_id, &BTreeSet::new(), now);
        }
        if let Some(entry) = self.bomb_impacts.get_mut(&deployable_id) {
            entry.pending = Some(PendingBombImpact { candidate_ids: candidate_ids.clone(), sent_at: now });
        }
        vec![Out::StructureDamageCandidatesRequest {
            session: impact.session,
            structure_id: impact.structure_id,
            module_id: impact.module_id,
            effect_id: impact.effect_id,
            activation_nonce: impact.activation_nonce,
            control_lease_generation: impact.control_lease_generation,
            cycle_id: impact.cycle_id,
            pulse_hop_ordinal: 0,
            candidate_ids,
        }]
    }

    /// The Host's correlated answer for a structure's bomb blast. Only the ships and structures it names, all from the request, are reached; a reply that
    /// names anything else, or repeats an ID, authorizes nothing and the bomb keeps waiting. None when no bomb waits on this correlation.
    pub(crate) fn settle_bomb_impact(
        &mut self,
        session: SessionId,
        correlation: (i64, i64, i32, u64, i64, u64, u32),
        allowed: &[i64],
        now: i64,
    ) -> Option<Vec<Out>> {
        let (
            structure_id,
            module_id,
            effect_id,
            activation_nonce,
            control_lease_generation,
            cycle_id,
            pulse_hop_ordinal,
        ) = correlation;
        let (id, candidates) = self.bomb_impacts.iter().find_map(|(id, impact)| {
            let pending = impact.pending.as_ref()?;
            (impact.session == session
                && impact.structure_id == structure_id
                && impact.module_id == module_id
                && impact.effect_id == effect_id
                && impact.activation_nonce == activation_nonce
                && impact.control_lease_generation == control_lease_generation
                && impact.cycle_id == cycle_id
                && pulse_hop_ordinal == 0)
                .then(|| (*id, pending.candidate_ids.clone()))
        })?;
        let granted: BTreeSet<i64> = allowed.iter().copied().collect();
        if granted.len() != allowed.len() || granted.iter().any(|ship| !candidates.contains(ship)) {
            return Some(Vec::new());
        }
        Some(self.finish_bomb_impact(id, &granted, now))
    }

    /// The bomb goes off with the blast reaching what was granted, and is taken out.
    fn finish_bomb_impact(&mut self, deployable_id: i64, granted: &BTreeSet<i64>, now: i64) -> Vec<Out> {
        self.bomb_impacts.remove(&deployable_id);
        let Some(deployable) = self.deployables.remove(&deployable_id) else { return Vec::new() };
        let mut out = self.detonate(&deployable, now, Some(granted));
        out.extend(self.depart(&deployable, true, now));
        out
    }

    /// The ships of the scene within `radius` of `centre` (their surface), but `exclude_ship`, which do not warp: each with its pilot's session, by ship id.
    pub(crate) fn ships_in_area(
        &self,
        centre: [f64; 3],
        radius: f64,
        exclude_ship: Option<i64>,
        at: i64,
    ) -> Vec<(SessionId, i64)> {
        let candidates = self.pilots.iter().map(|(other, pilot)| self.spatial_ship(*other, pilot));
        let mut inside: Vec<(SessionId, i64)> = crate::geo::SpatialQuery::surface_radius(
            centre,
            0.0,
            radius,
            1e-6,
            crate::geo::SpatialFilter {
                exclude_id: exclude_ship,
                exclude_warping: true,
                ..crate::geo::SpatialFilter::default()
            },
        )
        .execute(candidates)
        .into_iter()
        .filter(|hit| !self.is_tethered_session(hit.item, at))
        .filter_map(|hit| self.pilots.get(&hit.item).map(|pilot| (hit.item, pilot.ship.spec.ship_id)))
        .collect();
        inside.sort_by_key(|(_, ship_id)| *ship_id);
        inside
    }

    /// A bomb goes off: its blast reaches the ships of its area (a ship in its undock protection is spared the damage, as a smartbomb spares it), and what it
    /// does is what its kind says. A launcher that has left the scene makes no aggression: its bomb still goes off where it is. A structure's blast (`granted`
    /// is the Host's authorization) reaches only the ships and structures it names.
    fn detonate(&mut self, deployable: &Deployable, at: i64, granted: Option<&BTreeSet<i64>>) -> Vec<Out> {
        let Role::Bomb { kind, blast_radius, damage, ecm, neutralize } = &deployable.role else {
            return Vec::new();
        };
        let centre = [deployable.position.x, deployable.position.y, deployable.position.z];
        let reached =
            |ship_or_structure: i64| granted.is_none_or(|allowed| allowed.contains(&ship_or_structure));
        match (kind, granted) {
            (BombKind::Damage, _) => {
                // a controlled structure's bomb hurts through the structure (its own ship is not in the scene as a pilot); the retained hull of its
                // controller is not a target of the structure's blast
                let own_ship = self.bomb_own_ship(deployable);
                let structure = self.controlled_structure_id(deployable.launcher);
                let retained = self
                    .controlled_structures
                    .get(&deployable.launcher)
                    .and_then(|observer| observer.previous_ship_id);
                let mut out = Vec::new();
                for target in self.ships_in_area(centre, *blast_radius, Some(own_ship), at) {
                    if Some(target.1) == retained
                        || !reached(target.1)
                        || self.pilots.get(&target.0).is_none_or(|pilot| pilot.protected_until > at)
                    {
                        continue;
                    }
                    out.extend(match structure {
                        Some(structure_id) => self.damage_ship_from_structure(
                            deployable.launcher,
                            structure_id,
                            deployable.module_type,
                            target,
                            *damage,
                            at,
                        ),
                        None => {
                            self.damage_ship(deployable.launcher, deployable.module_type, target, *damage, at)
                        }
                    });
                }
                // the structures in the blast take the damage in their layers (a structure is no pilot: no aggression)
                for structure_in_blast in self.structures_within(centre, *blast_radius) {
                    if structure_in_blast != own_ship && reached(structure_in_blast) {
                        out.extend(self.damage_structure(structure_in_blast, *damage, at));
                    }
                }
                out
            }
            (BombKind::Ecm, None) => {
                self.jam_at(centre, 0.0, Some(deployable.ship_id), ecm, *blast_radius, at)
            }
            (BombKind::Neutralize, None) => {
                let mut out = Vec::new();
                for (target, _) in self.ships_in_area(centre, *blast_radius, Some(deployable.ship_id), at) {
                    let amount = neutralize * self.capacitor_warfare_multiplier(target);
                    out.extend(self.take_capacitor(target, amount, at).0);
                }
                out
            }
            // a structure's ECM or neutralizing blast is applied to the granted ships as the structure burst applies it (the same numbers and rules)
            (BombKind::Ecm, Some(allowed)) => {
                let allowed: Vec<i64> = allowed.iter().copied().collect();
                self.apply_structure_burst(
                    deployable.launcher,
                    deployable.module_id,
                    deployable.module_type,
                    &crate::modules::Remote::Jam { strength: *ecm },
                    0.0,
                    &allowed,
                    at,
                )
            }
            (BombKind::Neutralize, Some(allowed)) => {
                let allowed: Vec<i64> = allowed.iter().copied().collect();
                self.apply_structure_burst(
                    deployable.launcher,
                    deployable.module_id,
                    deployable.module_type,
                    &crate::modules::Remote::Neutralize { amount: *neutralize, signature_resolution: 0.0 },
                    0.0,
                    &allowed,
                    at,
                )
            }
        }
    }

    /// A stasis burst goes off as a cycle starts: every ship within `range` of the ship that runs it (its own ship spared) is webbed by `speed_factor` for
    /// `duration_ms`, through the web of the module (`Scene::hold_for`), and aggressed as a web is. Nothing checks who the ships belong to: the reference's burst
    /// has no such check that is written down (DEVIATIONS).
    pub(crate) fn stasis_burst(
        &mut self,
        session: SessionId,
        effect: &Effect,
        range: f64,
        duration_ms: f64,
        speed_factor: f64,
        at: i64,
    ) -> Vec<Out> {
        let Some(pilot) = self.pilots.get(&session) else { return Vec::new() };
        let (own, owner, p) = (pilot.ship.spec.ship_id, self.combat_owner(session), pilot.ship.position);
        let mut out = Vec::new();
        for target in self.ships_in_area([p.x, p.y, p.z], range, Some(own), at) {
            let target_type_id = self.pilots.get(&target.0).map_or(0, |pilot| pilot.ship.spec.type_id);
            self.aggressions.push(crate::weapons::Aggression {
                attacker_ship: own,
                target_ship: target.1,
                module_type_id: effect.module_type,
                source_owner_id: owner,
                target_type_id,
            });
            out.extend(self.hold_for(
                session,
                target.0,
                effect.module_id,
                crate::ewar::Lasting::Web { percent: speed_factor },
                duration_ms,
                at,
            ));
        }
        out
    }

    /// A fighter's LaunchBomb blast goes off at `centre` when its window ends (ADR 0011's area, decided for the fighter by EVE.js `executeFighterBombCycle`):
    /// every ship within `radius` of it (its surface) takes `damage` from `launcher`. No ship is spared, not even the launcher's own, and the undock
    /// protection is not read, as the reference does not read it (DEVIATIONS, Fighter LaunchBomb).
    pub(crate) fn blast_at(
        &mut self,
        launcher: SessionId,
        module_type: i32,
        centre: [f64; 3],
        radius: f64,
        damage: crate::health::Damage,
        at: i64,
    ) -> Vec<Out> {
        let mut out = Vec::new();
        for target in self.ships_in_area(centre, radius, None, at) {
            out.extend(self.damage_ship(launcher, module_type, target, damage, at));
        }
        out
    }

    /// Whether a warp-disruption source covers `position` at `now`: a probe still in space within its range, or a bubble (a ship's running field generator, see

    pub(crate) fn warp_disrupted_at(&self, position: Vec3, now: i64) -> bool {
        let bubble = self.pilots.values().any(|pilot| {
            let reach = distance(position, pilot.ship.position);
            pilot.modules.running.iter().filter_map(|run| run.effect.action.as_ref()).any(
                |action| matches!(action, crate::modules::Action::WarpBubble { range } if reach <= *range),
            )
        });
        bubble
            || self.deployables.values().any(|deployable| match deployable.role {
                Role::WarpSource { range } => {
                    deployable.ends_at > now && distance(position, deployable.position) <= range
                }
                Role::MobileWarpSource { range, active } => {
                    active && distance(position, deployable.position) <= range
                }
                Role::Bomb { .. }
                | Role::Beacon
                | Role::CynoField
                | Role::ScanProbe
                | Role::JumpBridge { .. }
                | Role::CynoBeacon { .. }
                | Role::CynoInhibitor { .. }
                | Role::MobileObservatory { .. }
                | Role::ControlTower { .. }
                | Role::DeadspacePocket
                | Role::AccelerationGate
                | Role::CapturePoint
                | Role::BattlefieldScoreboard
                | Role::AnalysisBeacon { .. } => false,
            })
    }
}

#[cfg(test)]
mod tests;

impl Scene {
    /// A CRAB the host keeps a record of is put in space where it lies (protocol 61). Given once: one already in space is left as it is. Its end is the host's.
    pub(crate) fn place_analysis_beacon(
        &mut self,
        placement: crate::link::AnalysisBeaconPlacement,
        now: i64,
    ) -> Vec<Out> {
        if self.deployables.contains_key(&placement.deployable_id) {
            return Vec::new();
        }
        let [x, y, z] = placement.position;
        let deployable = Deployable {
            id: placement.deployable_id,
            spec: Spec {
                charge_type_id: placement.type_id,
                group_id: placement.group_id,
                name: placement.name,
                radius: placement.radius,
            },
            role: Role::AnalysisBeacon { state: placement.state },
            position: Vec3::new(x, y, z),
            ends_at: i64::MAX,
            launcher: 0,
            ship_id: placement.ship_id,
            owner_id: placement.owner_id,
            corporation_id: placement.corporation_id,
            alliance_id: placement.alliance_id,
            module_id: 0,
            module_type: 0,
            watchers: BTreeSet::new(),
            slim_fields: BTreeMap::new(),
        };
        self.insert_deployable(deployable, now)
    }

    /// The host sets a CRAB's state: those who have it in view are told its slim item again (`OnSlimItemChange`, the whole slim item). A state already set is
    /// not told again, and a deployable that is not a CRAB is left alone.
    /// Whether a CRAB's link holds a command of a pilot (EVE.js `applyLinkRestrictionsToShip`): from the start of the link the ship is immobile until the link is
    /// complete, and it cannot warp, jump or dock until a minute after that (the status clear delay, `MOBILE_ANALYSIS_BEACON_LINK_STATUS_CLEAR_DELAY_MS`). A stop is
    /// never held.
    pub(crate) fn link_holds(&self, session: SessionId, command: &Command, now: i64) -> bool {
        let Some(pilot) = self.pilots.get(&session) else { return false };
        let ship = pilot.ship.spec.ship_id;
        let Some(link) = self.deployables.values().find_map(|d| match &d.role {
            Role::AnalysisBeacon { state } if state.link_state >= 2 && state.linked_ship_id == ship => {
                Some(*state)
            }
            _ => None,
        }) else {
            return false;
        };
        let status_until = link.link_complete_at + ticks(60_000.0);
        match command {
            Command::GotoDirection(_)
            | Command::AlignTo { .. }
            | Command::FollowBall { .. }
            | Command::Orbit { .. } => now < link.link_complete_at,
            Command::WarpTo { .. }
            | Command::WarpToPoint { .. }
            | Command::Jump { .. }
            | Command::CynoJump { .. }
            | Command::Dock { .. } => now < status_until,
            _ => false,
        }
    }

    /// The host sets a dynamic slim-item field of a deployable (`SetSlimField`). Those who have it in view are told its slim item again
    /// (`OnSlimItemChange`, the whole slim item). A value already set is not told again; a deployable not in space is left alone.
    pub(crate) fn set_slim_field(
        &mut self,
        deployable_id: i64,
        name: String,
        value: SlimValue,
        at: i64,
    ) -> Vec<Out> {
        let Some(deployable) = self.deployables.get_mut(&deployable_id) else { return Vec::new() };
        if deployable.slim_fields.get(&name) == Some(&value) {
            return Vec::new();
        }
        deployable.slim_fields.insert(name, value);
        let changed = deployable.clone();
        let stamp = self.next_stamp(at);
        let change = payload::action(
            stamp,
            "OnSlimItemChange",
            [id(changed.id), slim_item(&changed, category_of(&changed.role))],
        );
        self.missile_audience(changed.position)
            .into_iter()
            .map(|session| Self::update(session, vec![change.clone()]))
            .collect()
    }

    pub(crate) fn set_analysis_beacon_state(
        &mut self,
        deployable_id: i64,
        state: AnalysisBeaconState,
        at: i64,
    ) -> Vec<Out> {
        let Some(deployable) = self.deployables.get_mut(&deployable_id) else { return Vec::new() };
        let Role::AnalysisBeacon { state: current } = &mut deployable.role else { return Vec::new() };
        if *current == state {
            return Vec::new();
        }
        *current = state;
        let changed = deployable.clone();
        let stamp = self.next_stamp(at);
        let change = payload::action(
            stamp,
            "OnSlimItemChange",
            [id(changed.id), slim_item(&changed, category_of(&changed.role))],
        );
        self.missile_audience(changed.position)
            .into_iter()
            .map(|session| Self::update(session, vec![change.clone()]))
            .collect()
    }
}
