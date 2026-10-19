use super::*;

impl Scene {
    /// The pilot is told which of its ship's attributes changed (one notification for all of them), or nothing when none did.
    pub(crate) fn health_told(
        session: SessionId,
        owner: i64,
        ship: i64,
        when: i64,
        changes: &[(i32, f64, f64)],
    ) -> Option<Out> {
        if changes.is_empty() {
            return None;
        }
        let lines: Vec<Value> = changes
            .iter()
            .map(|&(attribute, new, old)| {
                attribute_change(owner, ship, attribute, when, number(new), number(old))
            })
            .collect();
        Some(Self::dogma_notify(session, "OnModuleAttributeChanges", vec![Value::List(lines)]))
    }

    /// What a pilot's ship has taken, and how full its capacitor is, at `now`.
    pub(crate) fn condition_of(pilot: &Pilot, now: i64) -> Condition {
        Condition {
            charge: pilot.modules.capacitor.as_ref().map_or(1.0, |c| c.ratio_at(now)),
            ..pilot.ship.health.condition_at(now)
        }
    }

    /// A hit of `raw` damage lands on the ship `ship_id` at `now`: its layers take it, the pilot is told the attributes that changed and everyone who
    /// can see the ship, has it locked or flies it is told its damage state. What the hit did comes back (None when the ship is not here or has
    /// nothing to hurt).
    pub fn damage(&mut self, ship_id: i64, raw: Damage, now: i64) -> (Vec<Out>, Option<Applied>) {
        self.damage_through(ship_id, raw, now, true)
    }

    /// `damage` for a damage-over-time tick: the layers take the damage as it is, whatever their resistances (`Health::apply_unresisted`).
    pub(crate) fn damage_unresisted(
        &mut self,
        ship_id: i64,
        raw: Damage,
        now: i64,
    ) -> (Vec<Out>, Option<Applied>) {
        self.damage_through(ship_id, raw, now, false)
    }

    /// An already-resolved damage amount lands as a scalar, with no inferred type distribution.
    pub(crate) fn damage_resolved(
        &mut self,
        ship_id: i64,
        amount: f64,
        now: i64,
    ) -> (Vec<Out>, Option<Applied>) {
        if self.is_tethered(ship_id, now) {
            return (Vec::new(), None);
        }
        let Some(session) = self
            .pilots
            .iter()
            .find(|(_, pilot)| pilot.ship.spec.ship_id == ship_id)
            .map(|(session, _)| *session)
        else {
            return (Vec::new(), None);
        };
        let resistance_bonus = self.link_effects_active(session, now);
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        let owner = pilot.ship.spec.character_id;
        let amount = if resistance_bonus {
            amount * (1.0 - crate::deployable::CRAB_LINK_RESISTANCE_BONUS)
        } else {
            amount
        };
        let Some(applied) = pilot.ship.health.apply_resolved_amount(now, amount) else {
            return (Vec::new(), None);
        };
        let mut out: Vec<Out> = Self::health_told(session, owner, ship_id, now, &applied.attribute_changes())
            .into_iter()
            .collect();
        if applied.changed
            && let Some(brains) = self.brains.as_mut()
        {
            brains.damage_changed(ship_id);
        }
        if applied.changed
            && let Some(subject) = self.subject(ship_id, now)
        {
            out.extend(self.announce_damage_state(&subject, now));
        }
        if !applied.destroyed {
            out.extend(self.tell_squadron_size(session, now));
        }
        (out, Some(applied))
    }

    fn damage_through(
        &mut self,
        ship_id: i64,
        raw: Damage,
        now: i64,
        resisted: bool,
    ) -> (Vec<Out>, Option<Applied>) {
        if self.is_tethered(ship_id, now) {
            return (Vec::new(), None);
        }
        let Some(session) = self.pilots.iter().find(|(_, p)| p.ship.spec.ship_id == ship_id).map(|(s, _)| *s)
        else {
            return (Vec::new(), None);
        };
        let resistance_bonus = resisted && self.link_effects_active(session, now);
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        let owner = pilot.ship.spec.character_id;
        let applied = if resistance_bonus {
            pilot.ship.health.apply_with_resistance_bonus(
                now,
                raw,
                crate::deployable::CRAB_LINK_RESISTANCE_BONUS,
            )
        } else if resisted {
            pilot.ship.health.apply(now, raw)
        } else {
            pilot.ship.health.apply_unresisted(now, raw)
        };
        let Some(applied) = applied else { return (Vec::new(), None) };
        let mut out: Vec<Out> = Self::health_told(session, owner, ship_id, now, &applied.attribute_changes())
            .into_iter()
            .collect();
        if applied.changed
            && let Some(brains) = self.brains.as_mut()
        {
            brains.damage_changed(ship_id);
        }
        if applied.changed
            && let Some(subject) = self.subject(ship_id, now)
        {
            out.extend(self.announce_damage_state(&subject, now));
        }
        // a squadron that lost members is told its size (one with none left is destroyed, and told so)
        if !applied.destroyed {
            out.extend(self.tell_squadron_size(session, now));
        }
        (out, Some(applied))
    }

    /// `amount` points are given back to a layer of the ship `ship_id` at `now` (a repairer, a booster, a remote repairer): the pilot is told the attributes that
    /// changed and everyone who can see the ship, has it locked or flies it is told its damage state when it changed. Nothing is told when nothing was
    /// mended (the layer is whole, or the ship is not here).
    pub(crate) fn mend(&mut self, ship_id: i64, layer: Layer, amount: f64, now: i64) -> Vec<Out> {
        let Some(session) = self.session_of(ship_id) else { return Vec::new() };
        let pilot = self.pilots.get_mut(&session).expect("the pilot is in the scene");
        let owner = pilot.ship.spec.character_id;
        let Some(applied) = pilot.ship.health.repair(now, layer, amount) else { return Vec::new() };
        let mut out: Vec<Out> = Self::health_told(session, owner, ship_id, now, &applied.attribute_changes())
            .into_iter()
            .collect();
        if applied.changed
            && let Some(subject) = self.subject(ship_id, now)
        {
            out.extend(self.announce_damage_state(&subject, now));
        }
        out
    }

    /// The host asks for the ship to be mended (`Heal`, command 18): full shield, armor, hull and capacitor. The pilot is told the attributes that
    /// changed and the capacitor, those who can see the ship are told its damage state when it changed, and the command is answered last.
    pub fn heal(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        const CODE: u8 = 18;
        let Some(pilot) = self.pilots.get_mut(&session) else {
            return vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }];
        };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let mended = pilot.ship.health.restore(now);
        let (before, after) = pilot.modules.capacitor.as_mut().map_or((0.0, 0.0), |c| c.refill(now));
        let mut out: Vec<Out> = Self::health_told(session, owner, ship_id, now, &mended.attribute_changes())
            .into_iter()
            .collect();
        if after > 0.0 {
            out.push(Self::charge_told(session, owner, ship_id, now, after, before));
        }
        if mended.changed
            && let Some(subject) = self.subject(ship_id, now)
        {
            out.extend(self.announce_damage_state(&subject, now));
        }
        out.push(Out::Accepted { session, command: CODE, at: now });
        out
    }

    /// The host asks what the ship has taken (`GetCondition`, command 19).
    pub fn condition(&mut self, session: SessionId, now: i64) -> Vec<Out> {
        const CODE: u8 = 19;
        if let Some(structure_id) = self.controlled_structure_id(session) {
            return match self
                .structure_body(structure_id)
                .and_then(|_| self.structure_health.get(&structure_id))
            {
                Some(health) => {
                    let Some(modules) = self.structure_modules.get(&structure_id) else {
                        return vec![Out::Refused {
                            session,
                            command: CODE,
                            reason: "STRUCTURE_SPEC_NOT_READY",
                        }];
                    };
                    let mut condition = health.condition_at(now);
                    condition.charge =
                        modules.modules.capacitor.as_ref().map_or(1.0, |capacitor| capacitor.ratio_at(now));
                    vec![Out::Condition { session, command: CODE, condition }]
                }
                None => vec![Out::Refused { session, command: CODE, reason: "STRUCTURE_NOT_FOUND" }],
            };
        }
        match self.pilots.get(&session) {
            Some(pilot) => {
                vec![Out::Condition { session, command: CODE, condition: Self::condition_of(pilot, now) }]
            }
            None => vec![Out::Refused { session, command: CODE, reason: "NO_SHIP_IN_SPACE" }],
        }
    }

    /// The shield's charge is told: now, and a tenth of a second before; the next time is half a second on while the shield is below full.
    pub(crate) fn tell_shield(&mut self, session: SessionId, at: i64) -> Vec<Out> {
        let Some(pilot) = self.pilots.get_mut(&session) else { return Vec::new() };
        let (owner, ship_id) = (pilot.ship.spec.character_id, pilot.ship.spec.ship_id);
        let health = &mut pilot.ship.health;
        let told = health.shield_told(at);
        health.next_sample = if health.shield_full_at(at) { None } else { Some(at + SAMPLE_EVERY) };
        told.into_iter()
            .filter_map(|(new, old)| {
                Self::health_told(session, owner, ship_id, at, &[(SHIELD_CHARGE, new, old)])
            })
            .collect()
    }
}
