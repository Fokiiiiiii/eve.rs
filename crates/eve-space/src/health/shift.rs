use super::*;

impl Health {
    /// The armor's resonances as they are now: the ship's own, each times the adaptive profile of a running reactive armor hardener, or all one while
    /// a Polarized weapon pins them (the pin wins: the profile is not applied over it).
    pub fn armor_resonance(&self) -> Damage {
        if self.vitals.armor_pinned {
            return [1.0; 4];
        }
        let base = self.vitals.resonances.armor;
        match self.shift {
            Some(shift) => std::array::from_fn(|i| base[i] * shift.profile[i]),
            None => base,
        }
    }

    /// The adaptive profile of the running reactive armor hardener, if one runs.
    pub fn shift_profile(&self) -> Option<Damage> {
        self.shift.map(|shift| shift.profile)
    }

    /// A reactive armor hardener starts on the ship: its resonances are `seed` (the module's own values, in the order of a damage vector) and move
    /// by `step` a cycle. Its tally starts from nothing, so a hardener that starts again starts from its seed.
    pub fn start_shift(&mut self, seed: Damage, step: f64) {
        self.shift = Some(Shift { profile: seed.map(|r| r.clamp(0.0, 1.0)), tally: [0.0; 4], step });
    }

    /// The hardener stops: the armor is the ship's own again, and nothing is kept for a later start.
    pub fn stop_shift(&mut self) {
        self.shift = None;
    }

    /// A cycle of the running hardener ends: its resonances move as the damage the armor took in the cycle says, and the tally starts again. The new
    /// profile when it moved (the armor's resonances then change), None when nothing runs, when it did not move, or when the armor is pinned (a pinned
    /// armor takes no shift and keeps no tally).
    pub fn shift_cycle(&mut self) -> Option<Damage> {
        let pinned = self.vitals.armor_pinned;
        let shift = self.shift.as_mut()?;
        let tally = std::mem::replace(&mut shift.tally, [0.0; 4]);
        if pinned {
            return None;
        }
        let next = crate::resistance_shift::next_profile(shift.profile, tally, shift.step)?;
        shift.profile = next;
        Some(next)
    }

    /// The ship has new numbers: its absolute damage stays the same, while capacitor charge keeps its fraction.
    pub fn respec(&mut self, vitals: Vitals, shield_recharge_ms: f64, now: i64) {
        let condition = self.condition_at(now);
        let before = self.vitals.layers();
        let after = vitals.layers();
        let shield_damage = (before.shield * (1.0 - condition.shield_charge)).max(0.0);
        let armor_damage = (before.armor * condition.armor_damage).max(0.0);
        let hull_damage = (before.structure * condition.damage).max(0.0);

        self.vitals = vitals;
        self.recharge_s = shield_recharge_ms / 1000.0;
        self.tau_ms = shield_recharge_ms / 5.0;
        self.condition = Condition {
            damage: if after.structure > 0.0 { hull_damage / after.structure } else { 0.0 },
            armor_damage: if after.armor > 0.0 { armor_damage / after.armor } else { 0.0 },
            shield_charge: if after.shield > 0.0 { 1.0 - shield_damage / after.shield } else { 1.0 },
            charge: condition.charge,
        };
        self.at = now;
    }
}
