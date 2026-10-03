//! `SceneWorld` as the dogma engine of the NPCs: their attributes (from the ship's spec and what it has taken), their locks, and the weapons they
//! fight with, which are effects of the NPC's own ship (`crate::ship::NpcWeapon`): a task activates one at a target and the scene runs it
//! (`crate::modules`, `crate::weapons`).

use eve_behaviors::consts::{
    ATTRIBUTE_ARMOR_DAMAGE, ATTRIBUTE_ARMOR_HP, ATTRIBUTE_DAMAGE, ATTRIBUTE_EM_DAMAGE,
    ATTRIBUTE_ENTITY_MISSILE_TYPE_ID, ATTRIBUTE_EXPLOSION_DELAY, ATTRIBUTE_EXPLOSIVE_DAMAGE,
    ATTRIBUTE_FALLOFF, ATTRIBUTE_HP, ATTRIBUTE_KINETIC_DAMAGE, ATTRIBUTE_MAX_RANGE,
    ATTRIBUTE_MAX_TARGET_RANGE, ATTRIBUTE_MAX_VELOCITY, ATTRIBUTE_MISSILE_ENTITY_FLIGHT_TIME_MULTIPLIER,
    ATTRIBUTE_MISSILE_ENTITY_VELOCITY_MULTIPLIER, ATTRIBUTE_RATE_OF_FIRE, ATTRIBUTE_SHIELD_CAPACITY,
    ATTRIBUTE_SHIELD_CHARGE, ATTRIBUTE_SIGNATURE_RADIUS, ATTRIBUTE_THERMAL_DAMAGE,
    EFFECT_MISSILE_LAUNCHING_FOR_ENTITY,
};
use eve_behaviors::env::{Dogma, EffectCycle, EffectInfo, WorldError, WorldResult};
use eve_behaviors::value::Value;

use super::world::SceneWorld;
use crate::modules::Effect;
use crate::npc::is_npc_session;
use crate::scene::{Out, SessionId};
use crate::ship::NpcWeapon;
use crate::targeting::Order;

/// `targetAttack`: the effect an NPC's turret runs.
pub const EFFECT_TARGET_ATTACK: i64 = 10;

/// `missileLaunchDuration`: the length of a cycle of `missileLaunchingForEntity` (its `durationAttributeID`).
const ATTRIBUTE_MISSILE_LAUNCH_DURATION: i64 = 506;
const ATTRIBUTE_MASS: i64 = 4;
const ATTRIBUTE_MAX_LOCKED_TARGETS: i64 = 192;
const ATTRIBUTE_SCAN_RESOLUTION: i64 = 564;
const ATTRIBUTE_CAPACITOR_CAPACITY: i64 = 482;
const ATTRIBUTE_DURATION: i64 = 73;
const ATTRIBUTE_TRACKING_SPEED: i64 = 160;
const ATTRIBUTE_OPTIMAL_SIG_RADIUS: i64 = 620;
const ATTRIBUTE_DAMAGE_MULTIPLIER: i64 = 64;

impl SceneWorld<'_> {
    /// The NPC whose weapon this module is (the module of an NPC's weapon is the NPC's ship), and the weapon.
    fn weapon_of_module(&self, module_id: i64) -> Option<(SessionId, NpcWeapon)> {
        let session = self.session_of(module_id)?;
        let npc = self.scene.pilots.get(&session)?.ship.npc.as_ref()?;
        npc.combat
            .weapons
            .iter()
            .find(|weapon| weapon.module_id == module_id)
            .map(|weapon| (session, weapon.clone()))
    }

    /// The weapons of the NPCs of the scene whose ship is of this type (the weapons of an NPC are the same as any other's of the type).
    fn npc_weapons_of_type(&self, type_id: i64) -> impl Iterator<Item = &NpcWeapon> {
        self.scene
            .pilots
            .values()
            .filter(move |pilot| i64::from(pilot.ship.spec.type_id) == type_id)
            .filter_map(|pilot| pilot.ship.npc.as_ref())
            .flat_map(|npc| npc.combat.weapons.iter())
    }

    /// What a weapon's own attributes are (the damage of a shot, its range, its tracking, the length of its cycle).
    fn weapon_attribute(weapon: &NpcWeapon, attribute_id: i64) -> Option<f64> {
        let turret = &weapon.turret;
        Some(match attribute_id {
            ATTRIBUTE_MAX_RANGE => turret.optimal_range,
            ATTRIBUTE_FALLOFF => turret.falloff,
            ATTRIBUTE_TRACKING_SPEED => turret.tracking_speed,
            ATTRIBUTE_OPTIMAL_SIG_RADIUS => turret.signature_resolution,
            ATTRIBUTE_RATE_OF_FIRE | ATTRIBUTE_DURATION | ATTRIBUTE_MISSILE_LAUNCH_DURATION => {
                weapon.cycle_ms
            }
            ATTRIBUTE_DAMAGE_MULTIPLIER => 1.0,
            ATTRIBUTE_EM_DAMAGE => turret.raw_damage[0],
            ATTRIBUTE_THERMAL_DAMAGE => turret.raw_damage[1],
            ATTRIBUTE_KINETIC_DAMAGE => turret.raw_damage[2],
            ATTRIBUTE_EXPLOSIVE_DAMAGE => turret.raw_damage[3],
            _ => return None,
        })
    }

    /// What a ship's attribute is now; what it has not is 0, the default of every attribute the ship does not set.
    fn ship_attribute(&self, session: SessionId, attribute_id: i64) -> f64 {
        let Some(pilot) = self.scene.pilots.get(&session) else { return 0.0 };
        let spec = &pilot.ship.spec;
        let layers = pilot.ship.health.layers_at(self.now);
        let vitals = pilot.ship.health.vitals();
        match attribute_id {
            ATTRIBUTE_MAX_VELOCITY => spec.max_velocity,
            ATTRIBUTE_MASS => spec.mass,
            ATTRIBUTE_MAX_TARGET_RANGE => spec.max_target_range,
            ATTRIBUTE_MAX_LOCKED_TARGETS => f64::from(spec.max_locked_targets),
            ATTRIBUTE_SCAN_RESOLUTION => spec.scan_resolution,
            ATTRIBUTE_SIGNATURE_RADIUS => spec.signature_radius,
            ATTRIBUTE_CAPACITOR_CAPACITY => spec.capacitor_capacity,
            ATTRIBUTE_SHIELD_CAPACITY => vitals.shield_capacity,
            ATTRIBUTE_SHIELD_CHARGE => layers.shield,
            ATTRIBUTE_ARMOR_HP => vitals.armor_hp,
            ATTRIBUTE_ARMOR_DAMAGE => (vitals.armor_hp - layers.armor).max(0.0),
            ATTRIBUTE_HP => vitals.structure_hp,
            ATTRIBUTE_DAMAGE => (vitals.structure_hp - layers.structure).max(0.0),
            _ => 0.0,
        }
    }

    /// The tasks' calls that end in an order to a ship: what the order made is kept for the host, less what is for an NPC.
    fn keep(&mut self, told: Vec<Out>) {
        self.out.extend(told.into_iter().filter(|out| !is_npc_session(out.session())));
    }
}

impl Dogma for SceneWorld<'_> {
    fn attribute_value(&self, item_id: i64, attribute_id: i64) -> WorldResult<f64> {
        if let Some((_, weapon)) = self.weapon_of_module(item_id)
            && let Some(value) = Self::weapon_attribute(&weapon, attribute_id)
        {
            return Ok(value);
        }
        let session = self.session_of(item_id).ok_or_else(|| WorldError::Key(item_id.to_string()))?;
        Ok(self.ship_attribute(session, attribute_id))
    }

    /// What the behaviors ask of a type: for a hull that launches missiles, the type of its missile and what that missile flies with. The hull's own
    /// multipliers are already in the missile of its weapon, so they are 1 here and the range the behaviors work out (speed times flight time) is the weapon's.
    fn type_attribute(&self, type_id: i64, attribute_id: i64) -> f64 {
        self.npc_weapons_of_type(type_id)
            .find_map(|weapon| {
                let missile = weapon.turret.missile.as_ref()?;
                Some(match attribute_id {
                    ATTRIBUTE_ENTITY_MISSILE_TYPE_ID => f64::from(missile.charge_type_id),
                    ATTRIBUTE_MISSILE_ENTITY_VELOCITY_MULTIPLIER
                    | ATTRIBUTE_MISSILE_ENTITY_FLIGHT_TIME_MULTIPLIER => 1.0,
                    _ => return None,
                })
            })
            .or_else(|| {
                // the missile's own type: the speed and the delay it explodes after, which is how far it goes
                self.scene
                    .pilots
                    .values()
                    .filter_map(|pilot| pilot.ship.npc.as_ref())
                    .flat_map(|npc| npc.combat.weapons.iter())
                    .find_map(|weapon| {
                        let missile = weapon
                            .turret
                            .missile
                            .as_ref()
                            .filter(|m| i64::from(m.charge_type_id) == type_id)?;
                        match attribute_id {
                            ATTRIBUTE_MAX_VELOCITY => Some(missile.max_velocity),
                            ATTRIBUTE_EXPLOSION_DELAY => Some(missile.flight_time_ms),
                            _ => None,
                        }
                    })
            })
            .unwrap_or(0.0)
    }

    /// An NPC's weapons are effects of its ship, so it is the ship's type that has them.
    fn type_has_effect(&self, type_id: i64, effect_id: i64) -> bool {
        self.npc_weapons_of_type(type_id).any(|weapon| i64::from(weapon.effect_id) == effect_id)
    }

    fn is_effect_active(&self, item_id: i64, effect_id: i64) -> bool {
        self.active_effect(item_id, effect_id).is_some()
    }

    fn targets(&self, item_id: i64) -> Vec<i64> {
        self.session_of(item_id).map(|session| self.scene.locked_by(session)).unwrap_or_default()
    }

    fn locked_targets(&self, item_id: i64) -> Vec<i64> {
        self.targets(item_id)
    }

    fn targets_ex(&self, item_id: i64) -> Vec<i64> {
        let Some(session) = self.session_of(item_id) else { return Vec::new() };
        let mut targets = self.scene.locked_by(session);
        targets.extend(self.scene.pilots[&session].targeting.pending_targets());
        targets
    }

    fn has_pending_target(&self, item_id: i64, target_id: Option<i64>) -> bool {
        let Some(session) = self.session_of(item_id) else { return false };
        let pending = self.scene.pilots[&session].targeting.pending_targets();
        match target_id {
            Some(target) => pending.contains(&target),
            None => !pending.is_empty(),
        }
    }

    fn is_targeting(&self, item_id: i64, target_id: Option<i64>) -> bool {
        let locked = self.targets(item_id);
        match target_id {
            Some(target) => locked.contains(&target),
            None => !locked.is_empty(),
        }
    }

    fn is_item_active(&self, item_id: i64) -> bool {
        self.session_of(item_id).is_some()
    }

    fn online_fitted_modules(&self, item_id: i64) -> Vec<(i64, i64, i64)> {
        let Some(session) = self.session_of(item_id) else { return Vec::new() };
        let modules = &self.scene.pilots[&session].ship.spec.modules;
        modules
            .iter()
            .map(|module| (module.item_id, i64::from(module.type_id), i64::from(module.flag)))
            .collect()
    }

    fn is_ship_piloted(&self, ship_id: i64) -> bool {
        self.session_of(ship_id)
            .and_then(|session| self.scene.pilots.get(&session))
            .is_some_and(|pilot| pilot.ship.npc.is_none() && pilot.ship.spec.character_id > 0)
    }

    fn active_effect_target_id(&self, item_id: i64, effect_id: i64) -> Option<Option<i64>> {
        self.active_effect(item_id, effect_id)
            .map(|(effect, _)| (effect.target_id > 0).then_some(effect.target_id))
    }

    fn active_effect_data(&self, item_id: i64, effect_id: i64) -> Option<EffectCycle> {
        self.active_effect(item_id, effect_id)
            .map(|(effect, started)| EffectCycle { start_time: started, duration_ms: effect.duration_ms })
    }

    fn effect_info(&self, effect_id: i64) -> Option<EffectInfo> {
        match effect_id {
            EFFECT_TARGET_ATTACK => Some(EffectInfo {
                duration_attribute_id: Some(ATTRIBUTE_RATE_OF_FIRE),
                range_attribute_id: Some(ATTRIBUTE_MAX_RANGE),
                falloff_attribute_id: Some(ATTRIBUTE_FALLOFF),
            }),
            // the effect of a launcher has a cycle and no range of its own: the missile goes as far as it flies
            EFFECT_MISSILE_LAUNCHING_FOR_ENTITY => Some(EffectInfo {
                duration_attribute_id: Some(ATTRIBUTE_MISSILE_LAUNCH_DURATION),
                range_attribute_id: None,
                falloff_attribute_id: None,
            }),
            _ => None,
        }
    }

    fn default_effect_id(&self, type_id: i64) -> Option<i64> {
        self.npc_weapons_of_type(type_id).next().map(|weapon| i64::from(weapon.effect_id))
    }

    fn effect_range_and_falloff_for_item(&self, item_id: i64, effect_id: i64) -> (Option<f64>, Option<f64>) {
        match self.weapon_of_module(item_id) {
            Some((_, weapon)) if i64::from(weapon.effect_id) == effect_id => {
                (Some(weapon.turret.optimal_range), Some(weapon.turret.falloff))
            }
            _ => (None, None),
        }
    }

    fn effect_range_with_falloff_multiplier(&self, effect_id: i64, item_id: i64, factor: f64) -> f64 {
        match self.effect_range_and_falloff_for_item(item_id, effect_id) {
            (Some(optimal), Some(falloff)) => optimal + falloff * factor,
            (Some(optimal), None) => optimal,
            _ => 0.0,
        }
    }

    fn effect_max_effective_range_with_falloff(&self, effect_id: i64, item_id: i64) -> f64 {
        self.effect_range_with_falloff_multiplier(effect_id, item_id, 1.0)
    }

    fn range_falloff_modifier_at_distance(&self, effect_id: i64, source_id: i64, distance: f64) -> f64 {
        match self.effect_range_and_falloff_for_item(source_id, effect_id) {
            (Some(optimal), Some(falloff)) if distance > optimal => {
                if falloff > 0.0 {
                    0.5_f64.powf(((distance - optimal) / falloff).powi(2))
                } else {
                    0.0
                }
            }
            _ => 1.0,
        }
    }

    fn activate_with_context(
        &mut self,
        _owner_id: i64,
        module_id: i64,
        effect_id: Option<i64>,
        target_id: Option<i64>,
        repeat: i64,
        _context: &Value,
    ) -> WorldResult<()> {
        let Some((session, weapon)) = self.weapon_of_module(module_id) else {
            return Err(WorldError::User("NotAModule".to_string()));
        };
        if effect_id.is_some_and(|effect| effect != i64::from(weapon.effect_id)) {
            return Err(WorldError::User("NoSuchEffect".to_string()));
        }
        let effect = Effect {
            module_id,
            module_type: weapon.turret.weapon_type_id,
            effect_id: weapon.effect_id,
            fx: weapon.turret.fx.clone(),
            duration_ms: weapon.cycle_ms,
            capacitor_need: weapon.capacitor_need,
            repeat: i32::try_from(repeat.max(1)).unwrap_or(i32::MAX),
            target_id: target_id.unwrap_or(0),
            turret: Some(weapon.turret),
            action: None,
            on: None,
            off: None,
            on_changes: Vec::new(),
            off_changes: Vec::new(),
        };
        let told = self.scene.activate(session, effect, self.now);
        // a launcher that is out of reach of its target is refused like any other activation (the tree has to bring the NPC into range)
        let refusal = told.iter().find_map(|out| match out {
            Out::Refused { reason, .. } | Out::TargetRefused { reason, .. } => Some((*reason).to_string()),
            _ => None,
        });
        self.keep(told);
        refusal.map_or(Ok(()), |reason| Err(WorldError::User(reason)))
    }

    fn stop_effect_force_repeating(&mut self, _effect_id: i64, module_id: i64) -> WorldResult<()> {
        self.stop_module(module_id);
        Ok(())
    }

    fn stop_effect(&mut self, _effect_id: i64, item_id: i64, _forced: bool) -> WorldResult<()> {
        self.stop_module(item_id);
        Ok(())
    }

    fn add_target_ex(&mut self, item_id: i64, target_id: i64) -> WorldResult<()> {
        let Some(session) = self.session_of(item_id) else {
            return Err(WorldError::Key(item_id.to_string()));
        };
        let told = self.scene.target_order(session, Order::Lock(target_id), self.now);
        let refusal = told.iter().find_map(|out| match out {
            Out::TargetRefused { reason, .. } | Out::Refused { reason, .. } => Some((*reason).to_string()),
            _ => None,
        });
        self.keep(told);
        refusal.map_or(Ok(()), |reason| Err(WorldError::User(reason)))
    }

    fn remove_target(&mut self, item_id: i64, target_id: i64) {
        if let Some(session) = self.session_of(item_id) {
            let told = self.scene.target_order(session, Order::Remove(vec![target_id]), self.now);
            self.keep(told);
        }
    }
}

impl SceneWorld<'_> {
    /// The running effect of an NPC's weapon, and when its cycle began.
    fn active_effect(&self, item_id: i64, effect_id: i64) -> Option<(&Effect, i64)> {
        let session = self.session_of(item_id)?;
        self.scene
            .pilots
            .get(&session)?
            .modules
            .running_effects()
            .find(|(effect, _)| effect.module_id == item_id && i64::from(effect.effect_id) == effect_id)
    }

    /// The pilot asks for a module to stop (at the end of its cycle).
    fn stop_module(&mut self, module_id: i64) {
        if let Some(session) = self.session_of(module_id) {
            let told = self.scene.deactivate(session, module_id, self.now);
            self.keep(told);
        }
    }
}
