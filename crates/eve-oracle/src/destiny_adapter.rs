//! Maps the JS value graph of the destiny fixtures to and from the typed
//! `eve-destiny` API, applying the JS coercions the reference applies.

use eve_destiny::ball::{
    BallEntity, BallError, ConfiguredFlags, Kind, LegacyMode, MiniBall, MiniBox, MiniCapsule, NpcClass,
    StaticAlliance, WarpState,
};
use eve_destiny::{DESTINY_SPACE_FRICTION, Motion, Vec3};

use crate::js::Js;
use crate::js_semantics::{
    build_vector, first_defined, first_truthy, id_ref, id_ref_skipping_blank, is_js_whitespace, member,
    parse_entity_id, positive_identity, to_finite, to_int32, to_number, trim_js, truthy,
};

/// `entity.x` of a JS number field; an absent or non-number field reads as
/// NaN, which the kernels treat like the reference's `toFiniteNumber` does.
fn number(node: Option<&Js>) -> f64 {
    match node {
        Some(Js::Num(value)) => *value,
        _ => f64::NAN,
    }
}

/// `{x, y, z}` (or nothing) as a vector whose missing parts read as NaN.
pub fn vector(node: Option<&Js>) -> Vec3 {
    Vec3::new(
        number(node.and_then(|n| n.get("x"))),
        number(node.and_then(|n| n.get("y"))),
        number(node.and_then(|n| n.get("z"))),
    )
}

pub fn vector_to_js(vector: Vec3) -> Js {
    Js::obj([("x", Js::Num(vector.x)), ("y", Js::Num(vector.y)), ("z", Js::Num(vector.z))])
}

pub fn motion_to_js(motion: Motion) -> Js {
    Js::obj([("position", vector_to_js(motion.position)), ("velocity", vector_to_js(motion.velocity))])
}

/// `friction` is a default parameter in JS: only an absent field selects the
/// default; a present non-finite value is replaced later by the same default.
fn friction(input: &Js) -> f64 {
    match input.get("friction") {
        None => DESTINY_SPACE_FRICTION,
        other => number(other),
    }
}

/// Run a `carbon.integrate` fixture input.
pub fn run_integrate(input: &Js) -> Js {
    motion_to_js(eve_destiny::integrate_carbon_motion(
        vector(input.get("position")),
        vector(input.get("velocity")),
        vector(input.get("acceleration")),
        number(input.get("mass")),
        friction(input),
        number(input.get("seconds")),
    ))
}

/// Run a `carbon.acceleration` fixture input.
pub fn run_acceleration(input: &Js) -> Js {
    vector_to_js(eve_destiny::derive_carbon_intrinsic_acceleration(
        vector(input.get("startVelocity")),
        vector(input.get("endVelocity")),
        number(input.get("mass")),
        friction(input),
        number(input.get("seconds")),
    ))
}

/// Run a `math.exp` fixture input.
pub fn run_exp(input: &Js) -> Js {
    Js::Num(eve_destiny::math::exp(number(input.get("x"))))
}

// ─── Ball encoding ──────────────────────────────────────────────────────────

const FORWARD: Vec3 = Vec3::new(1.0, 0.0, 0.0);

fn kind(node: Option<&Js>) -> Kind {
    match node {
        Some(Js::Str(text)) => match text.as_str() {
            "ship" => Kind::Ship,
            "missile" => Kind::Missile,
            "drone" => Kind::Drone,
            "fighter" => Kind::Fighter,
            "warpDisruptionProbe" => Kind::WarpDisruptionProbe,
            "forceField" => Kind::ForceField,
            "container" => Kind::Container,
            "wreck" => Kind::Wreck,
            "corpse" => Kind::Corpse,
            "unanchoredStructureHull" => Kind::UnanchoredStructureHull,
            "station" => Kind::Station,
            "stargate" => Kind::Stargate,
            "orbital" => Kind::Orbital,
            _ => Kind::Other,
        },
        _ => Kind::Other,
    }
}

/// `getLegacyFreeBallMode`: only a string names a mode.
fn legacy_mode(node: Option<&Js>) -> LegacyMode {
    let Some(Js::Str(text)) = node else {
        return LegacyMode::Stop;
    };
    match trim_js(text).to_uppercase().as_str() {
        "GOTO" => LegacyMode::Goto,
        "FOLLOW" => LegacyMode::Follow,
        "STOP" => LegacyMode::Stop,
        "WARP" => LegacyMode::Warp,
        "ORBIT" => LegacyMode::Orbit,
        "MISSILE" => LegacyMode::Missile,
        "MUSHROOM" => LegacyMode::Mushroom,
        "TROLL" => LegacyMode::Troll,
        "FIELD" => LegacyMode::Field,
        "FORMATION" => LegacyMode::Formation,
        _ => LegacyMode::Stop,
    }
}

/// `BALL_MODE[name]` for every mode the constants table names.
fn ball_mode_by_name(name: &str) -> Option<u8> {
    Some(match name {
        "GOTO" => 0,
        "FOLLOW" => 1,
        "STOP" => 2,
        "WARP" => 3,
        "ORBIT" => 4,
        "MISSILE" => 5,
        "MUSHROOM" => 6,
        "BOID" => 7,
        "TROLL" => 8,
        "MINIBALL" => 9,
        "FIELD" => 10,
        "RIGID" => 11,
        "FORMATION" => 12,
        _ => return None,
    })
}

/// `resolveConfiguredBallMode`'s candidate: `None` whenever the reference
/// would end up on its fallback (absent, unparsable or outside 0..=12).
fn destiny_ball_mode(node: Option<&Js>) -> Option<u8> {
    let value = node?;
    let named = match value {
        Js::Str(text) => ball_mode_by_name(&trim_js(text).to_uppercase()),
        _ => None,
    };
    let number = named.map_or_else(|| to_int32(Some(value), f64::NAN), f64::from);
    (0.0..=12.0).contains(&number).then_some(number as u8)
}

fn configured_flags(node: Option<&Js>) -> ConfiguredFlags {
    match node {
        None => ConfiguredFlags::Unset,
        Some(value) => {
            let number = to_number(Some(value));
            if number.is_finite() {
                ConfiguredFlags::Value(number.trunc())
            } else {
                ConfiguredFlags::NonFinite
            }
        }
    }
}

fn boolean(node: Option<&Js>) -> Option<bool> {
    match node {
        Some(Js::Bool(flag)) => Some(*flag),
        _ => None,
    }
}

fn warp_state(node: Option<&Js>) -> Option<WarpState> {
    if !truthy(node) {
        return None;
    }
    let get = |key: &str| node.and_then(|warp| warp.get(key));
    let native_command = match get("nativeWarpCommand") {
        Some(Js::Str(text)) if !text.is_empty() => trim_js(text).to_uppercase() == "GOTO",
        _ => false,
    };
    Some(WarpState {
        effect_stamp: to_number(get("effectStamp")),
        total_distance: to_finite(get("totalDistance"), 0.0),
        stop_distance: to_finite(get("stopDistance"), 0.0),
        warp_speed: truthy(get("warpSpeed")).then(|| to_number(get("warpSpeed"))),
        native_command_is_goto: native_command,
        raw_destination: truthy(get("rawDestination"))
            .then(|| build_vector(get("rawDestination"), Vec3::ZERO)),
        target_point: truthy(get("targetPoint")).then(|| build_vector(get("targetPoint"), Vec3::ZERO)),
    })
}

fn npc_class(node: Option<&Js>) -> NpcClass {
    match node {
        Some(Js::Str(text)) => match trim_js(text).to_lowercase().as_str() {
            "npc" => NpcClass::Npc,
            "concord" => NpcClass::Concord,
            "drifter" => NpcClass::Drifter,
            _ => NpcClass::Other,
        },
        _ => NpcClass::Other,
    }
}

/// `normalizeStaticTailBuffer`.
fn collision_tail(node: Option<&Js>) -> Option<Vec<u8>> {
    match node? {
        Js::Buf(bytes) => Some(bytes.clone()),
        Js::Arr(items) => items
            .iter()
            .map(|item| {
                let byte = to_number(Some(item)).trunc();
                (byte.is_finite() && (0.0..=255.0).contains(&byte)).then_some(byte as u8)
            })
            .collect(),
        Js::Str(text) => {
            let hex: String = text.chars().filter(|c| !is_js_whitespace(*c)).collect();
            if hex.is_empty()
                || !hex.len().is_multiple_of(2)
                || !hex.bytes().all(|byte| byte.is_ascii_hexdigit())
            {
                return None;
            }
            (0..hex.len()).step_by(2).map(|at| u8::from_str_radix(&hex[at..at + 2], 16).ok()).collect()
        }
        _ => None,
    }
}

/// `normalizeArray(a || b)`: the first truthy of the two spellings, if an array.
fn mini_list<'a>(entity: &'a Js, primary: &str, fallback: &str) -> &'a [Js] {
    match first_truthy(&[entity.get(primary), entity.get(fallback)]) {
        Some(Js::Arr(items)) => &items[..items.len().min(0xffff)],
        _ => &[],
    }
}

fn mini_ball(element: &Js) -> MiniBall {
    let source = Some(element);
    let center = if truthy(source) {
        first_truthy(&[member(source, "center"), member(source, "position"), source])
    } else {
        source
    };
    MiniBall { center: build_vector(center, Vec3::ZERO), radius: to_finite(member(source, "radius"), 0.0) }
}

/// `getMiniCapsulePoint`.
fn capsule_point(capsule: Option<&Js>, primary: &str, fallback: &str, prefix: &str) -> Vec3 {
    if !truthy(capsule) {
        return build_vector(capsule, Vec3::ZERO);
    }
    let candidates = [
        member(capsule, primary),
        member(capsule, fallback),
        member(capsule, &primary.to_lowercase()),
        member(capsule, &fallback.to_lowercase()),
    ];
    if let Some(found) = candidates.iter().flatten().find(|value| value.truthy()) {
        return build_vector(Some(found), Vec3::ZERO);
    }
    // The numeric spelling: {x: capsule.ax, y: capsule.ay, z: capsule.az}.
    Vec3::new(
        to_finite(member(capsule, &format!("{prefix}x")), 0.0),
        to_finite(member(capsule, &format!("{prefix}y")), 0.0),
        to_finite(member(capsule, &format!("{prefix}z")), 0.0),
    )
}

fn mini_capsule(element: &Js) -> MiniCapsule {
    let source = Some(element);
    MiniCapsule {
        a: capsule_point(source, "hemisphereA", "pointA", "a"),
        b: capsule_point(source, "hemisphereB", "pointB", "b"),
        radius: to_finite(member(source, "radius"), 0.0),
    }
}

/// `getMiniBoxVector`.
fn box_vector(mini_box: Option<&Js>, name: &str, prefix: &str) -> Vec3 {
    if !truthy(mini_box) {
        return build_vector(mini_box, Vec3::ZERO);
    }
    if let Some(found) = member(mini_box, name).filter(|value| value.truthy()) {
        return build_vector(Some(found), Vec3::ZERO);
    }
    Vec3::new(
        to_finite(member(mini_box, &format!("{prefix}0")), 0.0),
        to_finite(member(mini_box, &format!("{prefix}1")), 0.0),
        to_finite(member(mini_box, &format!("{prefix}2")), 0.0),
    )
}

fn mini_box(element: &Js) -> MiniBox {
    let source = Some(element);
    MiniBox {
        corner: box_vector(source, "corner", "c"),
        local_x: box_vector(source, "localX", "x"),
        local_y: box_vector(source, "localY", "y"),
        local_z: box_vector(source, "localZ", "z"),
    }
}

/// `resolveStaticBallHeaderCorporationID`.
fn static_corporation_id(entity: &Js) -> f64 {
    let explicit = to_int32(entity.get("staticBallCorporationID"), 0.0);
    if explicit > 0.0 {
        return explicit;
    }
    let corporation = to_int32(entity.get("corporationID"), 0.0);
    if corporation > 0.0 {
        return corporation;
    }
    let owner = to_int32(entity.get("ownerID"), 0.0);
    if owner >= 1_000_000.0 { owner } else { -1.0 }
}

/// The entity as the reference reads it, resolved into the typed model.
pub fn ball_entity(entity: &Js) -> BallEntity {
    if !matches!(entity, Js::Obj(_)) {
        return BallEntity::default();
    }
    let get = |key: &str| entity.get(key);
    let chain = |keys: &[&str]| -> Option<&Js> {
        let operands: Vec<Option<&Js>> = keys.iter().map(|key| get(key)).collect();
        first_defined(&operands)
    };

    let follow = chain(&["targetEntityID", "followTargetID", "followID", "followId", "parentEntityID"]);
    let mode_owner = chain(&[
        "ownerEntityID",
        "launcherEntityID",
        "sourceShipID",
        "sourceEntityID",
        "ownerBallID",
        "modeOwnerID",
        "ownerID",
        "parentEntityID",
    ]);
    let effect_stamp = first_defined(&[
        get("effectStamp"),
        get("destinyEffectStamp"),
        member(get("missileState"), "effectStamp"),
        member(get("warpState"), "effectStamp"),
    ]);
    let mushroom_span = first_defined(&[
        get("span"),
        get("mushroomSpan"),
        get("destinationSpan"),
        get("targetSpan"),
        member(get("targetPoint"), "x"),
    ]);
    let mass = match get("mass") {
        None | Some(Js::Null) => None,
        other => Some(to_number(other)).filter(|mass| mass.is_finite()),
    };
    let static_alliance = if to_int32(get("allianceID"), 0.0) > 0.0 {
        StaticAlliance::Id(get("allianceID").map_or(eve_destiny::ball::IdRef::Invalid, parse_entity_id))
    } else {
        StaticAlliance::None
    };

    BallEntity {
        item_id: id_ref(get("itemID")),
        kind: kind(get("kind")),
        legacy_mode: legacy_mode(get("mode")),
        destiny_ball_mode: destiny_ball_mode(get("destinyBallMode")),
        destiny_ball_flags: configured_flags(get("destinyBallFlags")),
        destiny_force_free: boolean(get("destinyForceFree")),
        is_free: boolean(get("isFree")),
        destiny_force_interactive: boolean(get("destinyForceInteractive")),
        is_massive: boolean(get("isMassive")),
        massive: boolean(get("massive")),
        radius: to_finite(get("radius"), 1.0),
        position: build_vector(get("position"), Vec3::ZERO),
        velocity: build_vector(get("velocity"), Vec3::ZERO),
        direction: build_vector(get("direction"), FORWARD),
        target_point: truthy(get("targetPoint")).then(|| build_vector(get("targetPoint"), Vec3::ZERO)),
        mass,
        inertia: to_finite(get("inertia"), 1.0),
        max_velocity: to_finite(get("maxVelocity"), 0.0),
        speed_fraction: to_finite(get("speedFraction"), 0.0),
        cloak_mode: to_int32(chain(&["isCloaked", "cloakMode", "cloaked"]), 0.0),
        harmonic: id_ref(chain(&["harmonic", "harmonicID", "mHarmonic"])),
        corporation_id: id_ref_skipping_blank(get("corporationID")),
        alliance_id: id_ref_skipping_blank(get("allianceID")),
        follow_target: id_ref(follow),
        mode_owner: id_ref(mode_owner),
        follow_range: to_finite(
            chain(&["followRange", "orbitDistance", "formationRange", "missileRange"]),
            0.0,
        ),
        mushroom_span: to_finite(mushroom_span, 0.0),
        effect_stamp: to_int32(effect_stamp, 0.0),
        warp_state: warp_state(get("warpState")),
        pending_warp: truthy(get("pendingWarp")),
        warp_speed_au: to_number(get("warpSpeedAU")),
        has_session: truthy(get("session")),
        pilot_positive: positive_identity(chain(&["pilotCharacterID", "characterID"])),
        controller_positive: positive_identity(get("controllerID")),
        owner_positive: positive_identity(get("ownerID")),
        native_npc: get("nativeNpc") == Some(&Js::Bool(true)),
        npc_class: if truthy(get("npcEntityType")) {
            npc_class(get("npcEntityType"))
        } else {
            NpcClass::Other
        },
        collision_tail: collision_tail(get("destinyCollisionTail")),
        mini_balls: mini_list(entity, "miniBalls", "miniballs").iter().map(mini_ball).collect(),
        mini_capsules: mini_list(entity, "miniCapsules", "minicapsules").iter().map(mini_capsule).collect(),
        mini_boxes: mini_list(entity, "miniBoxes", "miniboxes").iter().map(mini_box).collect(),
        static_ball_mass: to_finite(get("staticBallMass"), 10_000_000_000.0),
        static_ball_unknown_byte: to_int32(get("staticBallUnknownByte"), 0.0),
        static_alliance,
        static_corporation_id: static_corporation_id(entity),
        static_ball_unknown_int32: to_int32(get("staticBallUnknownInt32"), -1.0),
    }
}

/// Run a `ball.encode` fixture input: the encoded ball, or the failure kind.
pub fn run_ball(input: &Js) -> Result<Js, BallError> {
    eve_destiny::ball::encode_entity_ball(&ball_entity(input)).map(Js::Buf)
}
