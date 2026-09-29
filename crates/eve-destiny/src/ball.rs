//! Destiny ball wire encoding.
//!
//! Port of `server/src/space/destiny/stream/ballEncoding.js` and its helpers
//! (`ballConfig`, `freeBallHeader`, `staticBallTail`, `primitives`): what a
//! ball looks like in an AddBalls record, and which kind, mode and flags an
//! entity gets on the way there.
//!
//! The reference reads loosely typed entity objects: alias chains
//! (`a ?? b ?? c`), `toFiniteNumber` coercions, and identities that are only
//! rejected when the chosen ball mode actually needs them. This port takes a
//! [`BallEntity`] whose fields are already resolved (each alias chain reduced
//! to one field, each numeric coercion applied) and reproduces the reference's
//! *rules* on top of it. An identity that is present but malformed is carried
//! as [`IdRef::Invalid`] and only fails an encode that needs it, just as in
//! the reference.

use crate::carbon::Vec3;
use crate::math::{finite_or, js_round};

// ─── Wire constants (Carbon IDstConstants.h) ────────────────────────────────

/// `BALL_MODE`.
pub mod mode {
    pub const GOTO: u8 = 0;
    pub const FOLLOW: u8 = 1;
    pub const STOP: u8 = 2;
    pub const WARP: u8 = 3;
    pub const ORBIT: u8 = 4;
    pub const MISSILE: u8 = 5;
    pub const MUSHROOM: u8 = 6;
    pub const BOID: u8 = 7;
    pub const TROLL: u8 = 8;
    pub const MINIBALL: u8 = 9;
    pub const FIELD: u8 = 10;
    pub const RIGID: u8 = 11;
    pub const FORMATION: u8 = 12;
}

/// `BALL_FLAG`.
pub mod flag {
    pub const IS_FREE: u8 = 0x01;
    pub const IS_GLOBAL: u8 = 0x02;
    pub const IS_MASSIVE: u8 = 0x04;
    pub const IS_INTERACTIVE: u8 = 0x08;
    pub const IS_SPACEJUNK: u8 = 0x10;
    pub const HAS_MINIBOXES: u8 = 0x20;
    pub const HAS_MINIBALLS: u8 = 0x40;
    pub const HAS_MINICAPSULES: u8 = 0x80;
}

/// The flags a free ball may carry from configuration.
const FREE_WIRE_CONFIG_FLAGS: u8 =
    flag::IS_GLOBAL | flag::IS_MASSIVE | flag::IS_INTERACTIVE | flag::IS_SPACEJUNK;
const MINI_GEOMETRY_FLAG_MASK: u8 = flag::HAS_MINIBOXES | flag::HAS_MINIBALLS | flag::HAS_MINICAPSULES;

// ─── Errors ─────────────────────────────────────────────────────────────────

/// Why a ball could not be encoded. [`BallError::kind`] names the failure the
/// JS reference raises for the same input.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BallError {
    /// A required identity is absent or malformed (`TypeError`).
    Identity(&'static str),
    /// A wire integer outside its width (`RangeError` from Node's buffer writers).
    IntRange(&'static str),
    /// A signed-int64 identity outside its wire range (a plain `RangeError`).
    WireRange(&'static str),
}

impl BallError {
    pub fn kind(&self) -> &'static str {
        match self {
            Self::Identity(_) => "TypeError",
            Self::IntRange(_) => "RangeError:ERR_OUT_OF_RANGE",
            Self::WireRange(_) => "RangeError",
        }
    }
}

impl std::fmt::Display for BallError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Identity(what) => write!(f, "{what} must be an exact signed int64 identity"),
            Self::IntRange(what) => write!(f, "{what} does not fit its wire width"),
            Self::WireRange(what) => write!(f, "{what} is outside its wire range"),
        }
    }
}

impl std::error::Error for BallError {}

// ─── Entity model ───────────────────────────────────────────────────────────

/// An identity read from an entity field, before anything requires it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum IdRef {
    /// `undefined` or `null`.
    #[default]
    Absent,
    /// The empty string: not skipped by an `a ?? b` chain, but "no value" to
    /// the readers that treat it as absent.
    Blank,
    /// An exact signed int64.
    Valid(i64),
    /// An integer text or bigint outside the int64 range.
    OutOfRange,
    /// Anything else that is not an exact integer.
    Invalid,
}

impl IdRef {
    /// `parseEntityIDBigInt(x) !== null`: a usable identity.
    pub fn valid(self) -> Option<i64> {
        match self {
            Self::Valid(id) => Some(id),
            _ => None,
        }
    }

    /// `resolveOptionalEntityID(value, 0, label)`: 0 when absent or blank,
    /// an error when malformed.
    fn optional_or_zero(self, what: &'static str) -> Result<i64, BallError> {
        match self {
            Self::Absent | Self::Blank => Ok(0),
            Self::Valid(id) => Ok(id),
            _ => Err(BallError::Identity(what)),
        }
    }

    /// `requireEntityID(value, label)`.
    fn required(self, what: &'static str) -> Result<i64, BallError> {
        self.valid().ok_or(BallError::Identity(what))
    }
}

/// What kind of thing an entity is (`entity.kind`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Kind {
    Ship,
    Missile,
    Drone,
    Fighter,
    WarpDisruptionProbe,
    ForceField,
    Container,
    Wreck,
    Corpse,
    UnanchoredStructureHull,
    Station,
    Stargate,
    Orbital,
    /// Planets, asteroids, and everything else: rigid, global.
    #[default]
    Other,
}

/// The `entity.mode` names a free ball understands.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum LegacyMode {
    Goto,
    Follow,
    #[default]
    Stop,
    Warp,
    Orbit,
    Missile,
    Mushroom,
    Troll,
    Field,
    Formation,
}

impl LegacyMode {
    fn code(self) -> u8 {
        match self {
            Self::Goto => mode::GOTO,
            Self::Follow => mode::FOLLOW,
            Self::Stop => mode::STOP,
            Self::Warp => mode::WARP,
            Self::Orbit => mode::ORBIT,
            Self::Missile => mode::MISSILE,
            Self::Mushroom => mode::MUSHROOM,
            Self::Troll => mode::TROLL,
            Self::Field => mode::FIELD,
            Self::Formation => mode::FORMATION,
        }
    }
}

/// `entity.npcEntityType`, lower-cased and trimmed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum NpcClass {
    #[default]
    Other,
    Npc,
    Concord,
    Drifter,
}

/// `entity.destinyBallFlags`, as the reference reads it.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub enum ConfiguredFlags {
    /// `undefined`.
    #[default]
    Unset,
    /// A value that is not a finite number.
    NonFinite,
    /// A finite number (already `Math.trunc`ed).
    Value(f64),
}

/// The warp-related part of an entity (`entity.warpState`).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WarpState {
    /// `Number(effectStamp)`: NaN when absent or not numeric.
    pub effect_stamp: f64,
    /// `toFiniteNumber(totalDistance, 0)`.
    pub total_distance: f64,
    /// `toFiniteNumber(stopDistance, 0)`.
    pub stop_distance: f64,
    /// `warpSpeed` when it is truthy (as `Number(...)`; NaN if not numeric).
    pub warp_speed: Option<f64>,
    /// `nativeWarpCommand` is "GOTO" (trimmed, any case).
    pub native_command_is_goto: bool,
    /// `rawDestination`, when truthy.
    pub raw_destination: Option<Vec3>,
    /// `targetPoint`, when truthy.
    pub target_point: Option<Vec3>,
}

impl Default for WarpState {
    fn default() -> Self {
        Self {
            effect_stamp: f64::NAN,
            total_distance: 0.0,
            stop_distance: 0.0,
            warp_speed: None,
            native_command_is_goto: false,
            raw_destination: None,
            target_point: None,
        }
    }
}

/// An entity with every alias chain and numeric coercion already applied.
#[derive(Debug, Clone, PartialEq)]
pub struct BallEntity {
    pub item_id: IdRef,
    pub kind: Kind,
    /// `entity.mode` mapped to a name the free ball knows (Stop otherwise).
    pub legacy_mode: LegacyMode,
    /// `destinyBallMode` when it names or numbers a valid mode 0..=12 (`None`
    /// when absent, unparsable or out of range: the reference then falls back).
    pub destiny_ball_mode: Option<u8>,
    pub destiny_ball_flags: ConfiguredFlags,
    /// `destinyForceFree` / `isFree` (booleans only).
    pub destiny_force_free: Option<bool>,
    pub is_free: Option<bool>,
    pub destiny_force_interactive: Option<bool>,
    /// `isMassive` / `massive` (booleans only).
    pub is_massive: Option<bool>,
    pub massive: Option<bool>,

    /// `toFiniteNumber(radius, 1)`.
    pub radius: f64,
    pub position: Vec3,
    pub velocity: Vec3,
    /// `buildVector(direction, {1,0,0})`, not yet normalised.
    pub direction: Vec3,
    /// `targetPoint` when truthy.
    pub target_point: Option<Vec3>,
    /// The mass when set and finite; `None` selects the kind's default.
    pub mass: Option<f64>,
    pub inertia: f64,
    pub max_velocity: f64,
    pub speed_fraction: f64,
    /// `toInt32(isCloaked ?? cloakMode ?? cloaked, 0)`.
    pub cloak_mode: f64,
    pub harmonic: IdRef,
    pub corporation_id: IdRef,
    pub alliance_id: IdRef,

    pub follow_target: IdRef,
    pub mode_owner: IdRef,
    /// `toFiniteNumber(followRange ?? orbitDistance ?? ..., 0)`.
    pub follow_range: f64,
    pub mushroom_span: f64,
    /// `toInt32(effectStamp ?? destinyEffectStamp ?? ..., 0)`: an
    /// integer-valued number, possibly beyond int32 (which the write rejects).
    pub effect_stamp: f64,

    pub warp_state: Option<WarpState>,
    /// `Boolean(entity.pendingWarp)`.
    pub pending_warp: bool,
    /// `toFiniteNumber(warpSpeedAU, 0)` (as a number, NaN if not numeric).
    pub warp_speed_au: f64,

    /// `Boolean(entity.session)`.
    pub has_session: bool,
    /// A positive `pilotCharacterID ?? characterID`.
    pub pilot_positive: bool,
    pub controller_positive: bool,
    pub owner_positive: bool,
    pub native_npc: bool,
    pub npc_class: NpcClass,

    // Static (rigid) ball extras.
    pub collision_tail: Option<Vec<u8>>,
    pub mini_balls: Vec<MiniBall>,
    pub mini_capsules: Vec<MiniCapsule>,
    pub mini_boxes: Vec<MiniBox>,
    /// `toFiniteNumber(staticBallMass, 1e10)`.
    pub static_ball_mass: f64,
    /// `toInt32(staticBallUnknownByte, 0)`.
    pub static_ball_unknown_byte: f64,
    /// The static header's alliance: `toInt32(allianceID, 0) > 0` with the
    /// raw value to write (else the header writes -1).
    pub static_alliance: StaticAlliance,
    /// `resolveStaticBallHeaderCorporationID` (an integer-valued number).
    pub static_corporation_id: f64,
    /// `toInt32(staticBallUnknownInt32, -1)`.
    pub static_ball_unknown_int32: f64,
}

/// The alliance of a static ball header.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum StaticAlliance {
    /// `toInt32(allianceID, 0) <= 0`: write -1.
    #[default]
    None,
    /// Positive: write the raw identity (through `normalizeBigInt64`).
    Id(IdRef),
}

#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MiniBall {
    pub center: Vec3,
    pub radius: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MiniCapsule {
    pub a: Vec3,
    pub b: Vec3,
    pub radius: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct MiniBox {
    pub corner: Vec3,
    pub local_x: Vec3,
    pub local_y: Vec3,
    pub local_z: Vec3,
}

impl Default for BallEntity {
    fn default() -> Self {
        Self {
            item_id: IdRef::Absent,
            kind: Kind::Other,
            legacy_mode: LegacyMode::Stop,
            destiny_ball_mode: None,
            destiny_ball_flags: ConfiguredFlags::Unset,
            destiny_force_free: None,
            is_free: None,
            destiny_force_interactive: None,
            is_massive: None,
            massive: None,
            radius: 1.0,
            position: Vec3::ZERO,
            velocity: Vec3::ZERO,
            direction: Vec3::new(1.0, 0.0, 0.0),
            target_point: None,
            mass: None,
            inertia: 1.0,
            max_velocity: 0.0,
            speed_fraction: 0.0,
            cloak_mode: 0.0,
            harmonic: IdRef::Absent,
            corporation_id: IdRef::Absent,
            alliance_id: IdRef::Absent,
            follow_target: IdRef::Absent,
            mode_owner: IdRef::Absent,
            follow_range: 0.0,
            mushroom_span: 0.0,
            effect_stamp: 0.0,
            warp_state: None,
            pending_warp: false,
            warp_speed_au: 0.0,
            has_session: false,
            pilot_positive: false,
            controller_positive: false,
            owner_positive: false,
            native_npc: false,
            npc_class: NpcClass::Other,
            collision_tail: None,
            mini_balls: Vec::new(),
            mini_capsules: Vec::new(),
            mini_boxes: Vec::new(),
            static_ball_mass: 10_000_000_000.0,
            static_ball_unknown_byte: 0.0,
            static_alliance: StaticAlliance::None,
            static_corporation_id: -1.0,
            static_ball_unknown_int32: -1.0,
        }
    }
}

// ─── Wire writers (primitives.js) ───────────────────────────────────────────

struct Writer(Vec<u8>);

impl Writer {
    fn i64(&mut self, value: i64) {
        self.0.extend_from_slice(&value.to_le_bytes());
    }

    /// `pushInt32(toInt32(value, 0))`: an integer-valued number that must fit
    /// an int32 (Node's `writeInt32LE` throws otherwise).
    fn i32(&mut self, value: f64, what: &'static str) -> Result<(), BallError> {
        let value = finite_or(value, 0.0).trunc();
        if !(f64::from(i32::MIN)..=f64::from(i32::MAX)).contains(&value) {
            return Err(BallError::IntRange(what));
        }
        self.0.extend_from_slice(&(value as i32).to_le_bytes());
        Ok(())
    }

    /// `pushUInt8(value)`: `toInt32(value, 0) & 0xff`.
    fn u8(&mut self, value: f64) {
        self.0.push((crate::math::to_uint32(finite_or(value, 0.0).trunc()) & 0xff) as u8);
    }

    /// `pushFloat(value)`: `toFiniteNumber(value, 0)` rounded to float32.
    fn f32(&mut self, value: f64) {
        self.0.extend_from_slice(&(finite_or(value, 0.0) as f32).to_le_bytes());
    }

    /// `pushDouble(value)`.
    fn f64(&mut self, value: f64) {
        self.0.extend_from_slice(&finite_or(value, 0.0).to_le_bytes());
    }

    fn vec3(&mut self, vector: Vec3) {
        self.f64(vector.x);
        self.f64(vector.y);
        self.f64(vector.z);
    }

    fn count(&mut self, count: usize) {
        self.0.extend_from_slice(&(count as u16).to_le_bytes());
    }
}

/// `normalizeBigInt64` of an identity: what `pushBigInt64` accepts.
fn wire_i64(id: IdRef, what: &'static str) -> Result<i64, BallError> {
    match id {
        IdRef::Valid(value) => Ok(value),
        IdRef::OutOfRange => Err(BallError::WireRange(what)),
        _ => Err(BallError::Identity(what)),
    }
}

// ─── Rules (ballEncoding.js, ballConfig.js) ─────────────────────────────────

impl BallEntity {
    /// `resolveConfiguredBallMode(entity, fallback)`.
    fn configured_mode(&self, fallback: u8) -> u8 {
        let fallback = if fallback <= mode::FORMATION { fallback } else { mode::STOP };
        self.destiny_ball_mode.unwrap_or(fallback)
    }

    /// `resolveConfiguredBallFlags(entity, fallbackFlags)`.
    fn configured_flags(&self, fallback: u8) -> u8 {
        match self.destiny_ball_flags {
            ConfiguredFlags::Value(value) => (crate::math::to_uint32(value) & 0xff) as u8,
            ConfiguredFlags::Unset | ConfiguredFlags::NonFinite => fallback,
        }
    }

    fn explicit_missile_presentation_flag(&self) -> bool {
        match self.is_massive {
            Some(flag) => flag,
            None => self.massive == Some(true),
        }
    }

    /// `isTargetedLiveMissile`.
    fn is_targeted_live_missile(&self) -> bool {
        if self.kind != Kind::Missile {
            return false;
        }
        if self.configured_mode(self.legacy_mode.code()) != mode::MISSILE {
            return false;
        }
        matches!(self.follow_target.valid(), Some(target) if target != 0)
            && self.explicit_missile_presentation_flag()
    }

    /// `getFreeBallMode`.
    pub fn free_ball_mode(&self) -> u8 {
        let configured = self.configured_mode(self.legacy_mode.code());
        let supported = matches!(
            configured,
            mode::GOTO
                | mode::FOLLOW
                | mode::STOP
                | mode::WARP
                | mode::ORBIT
                | mode::MISSILE
                | mode::MUSHROOM
                | mode::TROLL
                | mode::FIELD
                | mode::FORMATION
        );
        let mode = if supported { configured } else { mode::STOP };
        if mode == mode::MISSILE && !self.is_targeted_live_missile() { mode::GOTO } else { mode }
    }

    /// `isFreeBallEntity`: a free ball (moves) rather than a rigid one.
    pub fn is_free_ball(&self) -> bool {
        if let Some(forced) = self.destiny_force_free {
            return forced;
        }
        if let Some(free) = self.is_free {
            return free;
        }
        matches!(
            self.kind,
            Kind::Ship
                | Kind::Missile
                | Kind::Drone
                | Kind::Fighter
                | Kind::WarpDisruptionProbe
                | Kind::ForceField
                | Kind::Container
                | Kind::Wreck
                | Kind::Corpse
                | Kind::UnanchoredStructureHull
        )
    }

    fn is_npc_ship(&self) -> bool {
        self.kind == Kind::Ship
            && (self.native_npc
                || matches!(self.npc_class, NpcClass::Npc | NpcClass::Concord | NpcClass::Drifter))
    }

    /// `isFreeBallInteractive`.
    fn is_free_ball_interactive(&self) -> bool {
        if !self.is_free_ball() {
            return false;
        }
        match self.kind {
            Kind::Container | Kind::Wreck | Kind::UnanchoredStructureHull | Kind::Corpse => return true,
            Kind::Missile | Kind::WarpDisruptionProbe => return false,
            _ => {}
        }
        if let Some(forced) = self.destiny_force_interactive {
            return forced;
        }
        if let ConfiguredFlags::Value(_) = self.destiny_ball_flags {
            return self.configured_flags(0) & flag::IS_INTERACTIVE != 0;
        }
        if matches!(self.kind, Kind::Drone | Kind::Fighter) {
            return self.controller_positive || self.owner_positive;
        }
        if self.is_npc_ship() {
            return false;
        }
        self.has_session || self.pilot_positive
    }

    /// `isEntityInActiveWarp`.
    fn in_active_warp(&self) -> bool {
        self.legacy_mode == LegacyMode::Warp
            && !self.pending_warp
            && !self.warp_state.is_some_and(|warp| warp.native_command_is_goto)
    }

    /// The warp effect stamp when it is a negative integer: the "aligning"
    /// presentation.
    fn negative_warp_stamp(&self) -> bool {
        self.warp_state.is_some_and(|warp| {
            warp.effect_stamp.is_finite() && warp.effect_stamp.fract() == 0.0 && warp.effect_stamp < 0.0
        })
    }

    /// `isShipWarpAlignmentBall`.
    fn is_ship_warp_alignment_ball(&self) -> bool {
        self.kind == Kind::Ship
            && self.free_ball_mode() == mode::WARP
            && self.pending_warp
            && self.negative_warp_stamp()
    }

    /// `getFreeBallFlags`.
    pub fn free_ball_flags(&self) -> u8 {
        if self.kind == Kind::Missile {
            return if self.is_targeted_live_missile() {
                flag::IS_FREE | flag::IS_MASSIVE
            } else {
                flag::IS_FREE
            };
        }
        let configured = self.configured_flags(0) & FREE_WIRE_CONFIG_FLAGS;
        let mut flags = flag::IS_FREE | configured;
        flags = if self.is_free_ball_interactive() {
            flags | flag::IS_INTERACTIVE
        } else {
            flags & !flag::IS_INTERACTIVE
        };

        if self.kind == Kind::Ship {
            // Carbon aliases a WARP record's total distance through
            // mLastCollision, so a proper WARP record stays out of the massive
            // path; 0x0d is reserved for the aligning presentation.
            flags = if self.is_ship_warp_alignment_ball() {
                flags | flag::IS_MASSIVE
            } else {
                flags & !flag::IS_MASSIVE
            };
        } else if let Some(massive) = self.is_massive.or(self.massive) {
            flags = if massive { flags | flag::IS_MASSIVE } else { flags & !flag::IS_MASSIVE };
        }
        flags
    }

    /// `getShipTargetPoint`.
    fn ship_target_point(&self) -> Vec3 {
        if let Some(point) = self.target_point {
            return point;
        }
        let direction = normalize(self.direction, Vec3::new(1.0, 0.0, 0.0));
        Vec3::new(
            self.position.x + direction.x * 1.0e16,
            self.position.y + direction.y * 1.0e16,
            self.position.z + direction.z * 1.0e16,
        )
    }

    /// `getDestinyWarpTargetPoint`.
    fn warp_target_point(&self) -> Vec3 {
        let preparing = !self.in_active_warp() || self.negative_warp_stamp();
        if let Some(warp) = &self.warp_state {
            // Preparing clients still run RealWarp and need the raw
            // destination plus minimum range.
            if let (true, Some(raw)) = (preparing, warp.raw_destination) {
                return raw;
            }
            if let Some(point) = warp.target_point {
                return point;
            }
        }
        self.ship_target_point()
    }

    /// `getShipWarpFactor`.
    fn warp_factor(&self) -> i64 {
        let raw = match self.warp_state.and_then(|warp| warp.warp_speed) {
            Some(speed) => speed,
            None if finite_or(self.warp_speed_au, 0.0) > 0.0 => js_round(self.warp_speed_au * 1000.0),
            None => 3000.0,
        };
        // toInt32(raw, 3000)
        let factor = finite_or(raw, 3000.0).trunc();
        if (0.0..=4_294_967_295.0).contains(&factor) { factor as i64 } else { 3000 }
    }

    /// `getFreeBallMode` mass fallback: containers and wrecks are light.
    fn free_ball_mass(&self) -> f64 {
        let fallback =
            if matches!(self.kind, Kind::Container | Kind::Wreck) { 10_000.0 } else { 1_000_000.0 };
        self.mass.unwrap_or(fallback)
    }
}

/// `normalizeVector(source, fallback)`, with `source` already run through
/// `buildVector`.
fn normalize(vector: Vec3, fallback: Vec3) -> Vec3 {
    let magnitude = (vector.x * vector.x + vector.y * vector.y + vector.z * vector.z).sqrt();
    if !magnitude.is_finite() || magnitude <= 0.0 {
        return fallback;
    }
    Vec3::new(vector.x / magnitude, vector.y / magnitude, vector.z / magnitude)
}

// ─── Encoding ───────────────────────────────────────────────────────────────

/// `encodeEntityBall`: a free ball if the entity moves, else a rigid ball.
pub fn encode_entity_ball(entity: &BallEntity) -> Result<Vec<u8>, BallError> {
    if entity.is_free_ball() { encode_free_ball(entity) } else { encode_rigid_ball(entity) }
}

/// `encodeFreeBall`.
pub fn encode_free_ball(entity: &BallEntity) -> Result<Vec<u8>, BallError> {
    let mut out = Writer(Vec::with_capacity(160));
    let ball_mode = entity.free_ball_mode();

    out.i64(entity.item_id.required("Destiny ball itemID")?);
    out.u8(f64::from(ball_mode));
    out.f32(entity.radius);
    out.vec3(entity.position);
    out.u8(f64::from(entity.free_ball_flags()));
    out.f64(entity.free_ball_mass());
    out.u8(entity.cloak_mode);

    // appendFreeBallOwnershipHeader
    out.i64(match entity.harmonic {
        IdRef::Absent | IdRef::Blank => -1,
        other => other.required("Destiny harmonic")?,
    });
    out.i32(optional_int32(entity.corporation_id, "corporationID")?, "corporationID")?;
    out.i32(optional_int32(entity.alliance_id, "allianceID")?, "allianceID")?;

    out.f32(entity.max_velocity);
    out.vec3(entity.velocity);
    out.f32(entity.inertia);
    out.f32(entity.speed_fraction);
    out.0.push(0xff);

    match ball_mode {
        mode::GOTO => out.vec3(entity.ship_target_point()),
        mode::FOLLOW | mode::ORBIT => {
            out.i64(entity.follow_target.optional_or_zero("Destiny follow target")?);
            out.f32(entity.follow_range);
        }
        mode::FORMATION => {
            out.i64(entity.follow_target.optional_or_zero("Destiny follow target")?);
            out.f32(entity.follow_range);
            out.i32(entity.effect_stamp, "effect stamp")?;
        }
        mode::MISSILE => {
            let target = entity.ship_target_point();
            out.i64(entity.follow_target.optional_or_zero("Destiny follow target")?);
            out.f32(entity.follow_range);
            out.i64(entity.mode_owner.optional_or_zero("Destiny mode owner")?);
            out.i32(entity.effect_stamp, "effect stamp")?;
            out.vec3(target);
        }
        mode::WARP => {
            let target = entity.warp_target_point();
            let warp = entity.warp_state.unwrap_or_default();
            out.vec3(target);
            out.i32(warp.effect_stamp, "warp effect stamp")?;
            out.f64(warp.total_distance);
            out.f64(warp.stop_distance);
            out.i64(entity.warp_factor());
        }
        mode::MUSHROOM => {
            out.f32(entity.follow_range);
            out.f64(entity.mushroom_span);
            out.i32(entity.effect_stamp, "effect stamp")?;
            out.i64(entity.mode_owner.optional_or_zero("Destiny mode owner")?);
        }
        mode::TROLL => out.i32(entity.effect_stamp, "effect stamp")?,
        _ => {}
    }
    Ok(out.0)
}

/// `resolveOptionalInt32`: -1 when absent, an int32 identity otherwise.
fn optional_int32(id: IdRef, what: &'static str) -> Result<f64, BallError> {
    match id {
        IdRef::Absent | IdRef::Blank => Ok(-1.0),
        IdRef::Valid(value) if i32::try_from(value).is_ok() => Ok(value as f64),
        _ => Err(BallError::Identity(what)),
    }
}

/// `encodeRigidBall`.
pub fn encode_rigid_ball(entity: &BallEntity) -> Result<Vec<u8>, BallError> {
    let mut out = Writer(Vec::with_capacity(64));
    let ball_mode = entity.configured_mode(mode::RIGID);
    let tail = static_tail(entity, ball_mode)?;

    out.i64(entity.item_id.required("Destiny ball itemID")?);
    out.u8(f64::from(ball_mode));
    out.f32(entity.radius);
    out.vec3(entity.position);
    out.u8(f64::from(static_flags(entity)));
    out.0.extend_from_slice(&tail);
    Ok(out.0)
}

// ─── Static (rigid) balls (staticBallTail.js) ───────────────────────────────

fn static_fallback_flags(entity: &BallEntity) -> u8 {
    match entity.kind {
        Kind::Station | Kind::Stargate => flag::IS_GLOBAL | flag::IS_MASSIVE,
        Kind::Orbital => flag::IS_GLOBAL | flag::IS_INTERACTIVE,
        Kind::Container | Kind::Wreck => flag::IS_INTERACTIVE,
        _ => flag::IS_GLOBAL,
    }
}

fn mini_geometry_flags(entity: &BallEntity) -> u8 {
    (if entity.mini_boxes.is_empty() { 0 } else { flag::HAS_MINIBOXES })
        | (if entity.mini_balls.is_empty() { 0 } else { flag::HAS_MINIBALLS })
        | (if entity.mini_capsules.is_empty() { 0 } else { flag::HAS_MINICAPSULES })
}

fn has_explicit_tail(entity: &BallEntity) -> bool {
    entity.collision_tail.as_ref().is_some_and(|tail| !tail.is_empty())
}

/// `getStaticBallFlags`.
fn static_flags(entity: &BallEntity) -> u8 {
    let configured = entity.configured_flags(static_fallback_flags(entity)) & !flag::IS_FREE;
    if has_explicit_tail(entity) {
        return configured;
    }
    (configured & !MINI_GEOMETRY_FLAG_MASK) | mini_geometry_flags(entity)
}

/// `resolveStaticBallTail(entity).tail`.
fn static_tail(entity: &BallEntity, ball_mode: u8) -> Result<Vec<u8>, BallError> {
    if let Some(tail) = entity.collision_tail.as_ref().filter(|tail| !tail.is_empty()) {
        return Ok(tail.clone());
    }

    let mut out = Writer(Vec::new());
    if ball_mode == mode::RIGID {
        out.0.push(0xff);
    } else {
        // buildStaticBallHeader
        out.f64(entity.static_ball_mass);
        out.u8(entity.static_ball_unknown_byte);
        out.i64(match entity.static_alliance {
            StaticAlliance::None => -1,
            StaticAlliance::Id(id) => wire_i64(id, "static ball alliance")?,
        });
        out.i32(entity.static_corporation_id, "static ball corporation")?;
        out.i32(entity.static_ball_unknown_int32, "static ball int32")?;
        out.0.push(0xff);
    }

    // appendMiniGeometrySections
    if !entity.mini_balls.is_empty() {
        out.count(entity.mini_balls.len());
        for ball in &entity.mini_balls {
            out.vec3(ball.center);
            out.f32(ball.radius);
        }
    }
    if !entity.mini_capsules.is_empty() {
        out.count(entity.mini_capsules.len());
        for capsule in &entity.mini_capsules {
            out.vec3(capsule.a);
            out.vec3(capsule.b);
            out.f32(capsule.radius);
        }
    }
    if !entity.mini_boxes.is_empty() {
        out.count(entity.mini_boxes.len());
        for mini_box in &entity.mini_boxes {
            out.vec3(mini_box.corner);
            out.vec3(mini_box.local_x);
            out.vec3(mini_box.local_y);
            out.vec3(mini_box.local_z);
        }
    }
    Ok(out.0)
}
