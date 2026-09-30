//! What a ship leaves when it is destroyed: a wreck, a ball that does not move until it is gone. The reference makes the wreck an item of its own
//! (the dead ship's type says which: `Caldari Frigate Wreck`), gives whoever may loot it the rights to it (`lootRights`), and takes it out of space
//! after `spaceDebrisLifetimeMs` (two hours). The client sees it as a ball of kind wreck with a slim item; nothing about it moves.

use eve_destiny::{BallEntity, IdRef, Kind, Vec3};
use eve_marshal::Value;

use crate::scene::TICKS_PER_SECOND;
use crate::wire::{id, int, list, real, text, tuple};

/// The group of the wrecks, and their category (`Celestial`).
pub const WRECK_GROUP: i32 = 186;
pub const WRECK_CATEGORY: i32 = 2;

/// How long a wreck stays in space: the reference's `spaceDebrisLifetimeMs`, two hours.
pub const WRECK_LIFETIME: i64 = 2 * 60 * 60 * TICKS_PER_SECOND;

/// The effect the client plays where a ship is destroyed (`DESTRUCTION_EFFECT_EXPLOSION`).
pub const DESTRUCTION_EFFECT_EXPLOSION: i64 = 3;

/// The wreck a ship of some type leaves.
#[derive(Debug, Clone, PartialEq)]
pub struct WreckKind {
    pub type_id: i32,
    pub name: String,
    pub graphic_id: Option<i32>,
}

/// Who may take what is in a wreck (`lootRights`: the character, the corporation, the fleet, and whether it was abandoned).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct LootRights {
    pub character_id: i64,
    pub corporation_id: i64,
}

/// A wreck in a scene.
#[derive(Debug, Clone, PartialEq)]
pub struct Wreck {
    pub id: i64,
    pub kind: WreckKind,
    pub position: Vec3,
    pub direction: Vec3,
    pub radius: f64,
    pub owner_id: i64,
    pub corporation_id: i64,
    pub alliance_id: i64,
    pub war_faction_id: i64,
    pub rights: LootRights,
    /// The ship that was destroyed.
    pub launcher_id: i64,
    /// Nothing in it to take.
    pub is_empty: bool,
    /// When it goes.
    pub expires_at: i64,
}

impl Wreck {
    /// The ball the destiny stream describes the wreck with.
    pub fn ball(&self) -> BallEntity {
        BallEntity {
            item_id: IdRef::Valid(self.id),
            kind: Kind::Wreck,
            radius: self.radius,
            position: self.position,
            direction: self.direction,
            // a wreck of a ship that had no alliance says so with -1 (a ship's ball writes its 0)
            corporation_id: if self.corporation_id > 0 {
                IdRef::Valid(self.corporation_id)
            } else {
                IdRef::Absent
            },
            alliance_id: if self.alliance_id > 0 { IdRef::Valid(self.alliance_id) } else { IdRef::Absent },
            owner_positive: self.owner_id > 0,
            ..BallEntity::default()
        }
    }

    /// The entries of the slim item, in the order the reference gives them.
    pub fn slim_entries(&self) -> Vec<(&'static str, Value)> {
        let mut entries = vec![
            ("itemID", id(self.id)),
            ("typeID", int(i64::from(self.kind.type_id))),
            ("ownerID", id(self.owner_id)),
            ("name", text(self.kind.name.clone())),
            ("groupID", int(i64::from(WRECK_GROUP))),
            ("categoryID", int(i64::from(WRECK_CATEGORY))),
        ];
        if let Some(graphic) = self.kind.graphic_id {
            entries.push(("graphicID", int(i64::from(graphic))));
        }
        entries.extend([
            ("corpID", id(self.corporation_id)),
            ("allianceID", id(self.alliance_id)),
            ("warFactionID", id(self.war_faction_id)),
            ("securityStatus", int(0)),
            ("isEmpty", int(i64::from(self.is_empty))),
            (
                "lootRights",
                list([
                    id(self.rights.character_id),
                    id(self.rights.corporation_id),
                    Value::None,
                    Value::Bool(false),
                ]),
            ),
            ("launcherID", id(self.launcher_id)),
        ]);
        entries.push(("dunRotation", tuple(turn_of(self.direction).map(number))));
        entries
    }
}

/// A rotation as the client reads it: whole angles are ints.
fn number(value: f64) -> Value {
    if value.fract() == 0.0 && value.abs() < 1e9 { int(value as i64) } else { real(value) }
}

/// The turn the reference gives a wreck (`buildDunRotationFromDirection` of `wreckUtils.js`, to six places): the yaw, `atan2(x, z)`, and the pitch,
/// `-asin(y)`, in degrees, of the direction the dead ship faced (`(1, 0, 0)` when it had none), and no roll. Unlike a stargate's, the yaw is not
/// brought into the range 0 to 360.
fn turn_of(direction: Vec3) -> [f64; 3] {
    let length = (direction.x * direction.x + direction.y * direction.y + direction.z * direction.z).sqrt();
    let forward = if length > 0.0 && length.is_finite() {
        Vec3::new(direction.x / length, direction.y / length, direction.z / length)
    } else {
        Vec3::new(1.0, 0.0, 0.0)
    };
    let round = |value: f64| (value * 1e6).round() / 1e6;
    let yaw = forward.x.atan2(forward.z).to_degrees();
    let pitch = -forward.y.clamp(-1.0, 1.0).asin().to_degrees();
    [round(yaw), round(pitch), 0.0]
}

#[cfg(test)]
mod tests {
    use super::*;
    use eve_destiny::encode_entity_ball;

    /// What the reference sent for the wreck of a hull that `/deathtest` blew up (`oracle/fixtures/scenarios/death.json`, the third update of the
    /// step "the watcher detonates a hull beside both"): the ball in the `AddBalls2` buffer, without its header (the kind and the stamp).
    const RECORDED_BALL: &str = "6fb5089b15090000\
        0200003c425fc833c6022952426c5427b7db5708c285e10f7e6c3c424209000000000088c34000ffffffffffffffff46420f00ffffffff000000000000000000000000000000000000000000000000000000000000803f00000000ff";

    fn hex(text: &str) -> Vec<u8> {
        let text: String = text.split_whitespace().collect();
        (0..text.len()).step_by(2).map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap()).collect()
    }

    fn recorded() -> Wreck {
        // the direction the recorded turn came from: yaw -38.03078, pitch -0.748281
        let (yaw, pitch) = ((-38.03078_f64).to_radians(), (-0.748281_f64).to_radians());
        Wreck {
            id: 9_988_400_002_415,
            kind: WreckKind {
                type_id: 26_506,
                name: "Caldari Frigate Wreck".to_string(),
                graphic_id: Some(3117),
            },
            position: Vec3::new(311_989_835_983.130_8, -13_069_154_020.916_222, 156_646_308_895.761_87),
            direction: Vec3::new(yaw.sin() * pitch.cos(), -pitch.sin(), yaw.cos() * pitch.cos()),
            radius: 47.0,
            owner_id: 140_000_393,
            corporation_id: 1_000_006,
            alliance_id: 0,
            war_faction_id: 500_001,
            rights: LootRights { character_id: 140_000_393, corporation_id: 1_000_006 },
            launcher_id: 4_200_000_000_000_000,
            is_empty: true,
            expires_at: 0,
        }
    }

    #[test]
    fn the_ball_is_the_one_the_reference_sent_for_a_wreck() {
        let sent = encode_entity_ball(&recorded().ball()).unwrap();
        assert_eq!(
            sent,
            hex(RECORDED_BALL),
            "{}",
            sent.iter().map(|b| format!("{b:02x}")).collect::<String>()
        );
    }

    #[test]
    fn the_slim_item_has_what_the_reference_gave_it_in_its_order() {
        let entries = recorded().slim_entries();
        let names: Vec<&str> = entries.iter().map(|(name, _)| *name).collect();
        assert_eq!(
            names,
            [
                "itemID",
                "typeID",
                "ownerID",
                "name",
                "groupID",
                "categoryID",
                "graphicID",
                "corpID",
                "allianceID",
                "warFactionID",
                "securityStatus",
                "isEmpty",
                "lootRights",
                "launcherID",
                "dunRotation"
            ]
        );
        let value = |key: &str| entries.iter().find(|(name, _)| *name == key).unwrap().1.clone();
        assert_eq!(value("dunRotation"), tuple([real(-38.03078), real(-0.748281), int(0)]));
        assert_eq!(
            value("lootRights"),
            list([id(140_000_393), id(1_000_006), Value::None, Value::Bool(false)])
        );
        assert_eq!(value("isEmpty"), int(1));
        assert_eq!(value("launcherID"), id(4_200_000_000_000_000));
    }

    #[test]
    fn a_ship_without_a_direction_leaves_a_wreck_turned_along_x() {
        assert_eq!(turn_of(Vec3::ZERO), [90.0, 0.0, 0.0]);
    }
}
