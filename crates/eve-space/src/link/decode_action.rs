use super::*;
use crate::missiles::Blast;

/// A deployable's charge: its type, its group, its name and the radius of its ball (a positive number).
fn read_deployable(reader: &mut Reader) -> Result<Spec, DecodeError> {
    let spec = Spec {
        charge_type_id: reader.i32()?,
        group_id: reader.i32()?,
        name: reader.text()?,
        radius: reader.f64()?,
    };
    if !spec.radius.is_finite() || spec.radius <= 0.0 {
        return Err(DecodeError::BadValue("deployable"));
    }
    Ok(spec)
}

pub(super) fn read_action(reader: &mut Reader) -> Result<Action, DecodeError> {
    match reader.u8()? {
        0 => {
            let layer = read_layer(reader)?;
            let when = read_applies(reader)?;
            Ok(Action::Repair { layer, when, amount: reader.f64()? })
        }
        1 => {
            let when = read_applies(reader)?;
            let reach = Reach { optimal: reader.f64()?, falloff: reader.f64()? };
            let jam = reader.text()?;
            let what = match reader.u8()? {
                0 => {
                    let layer = read_layer(reader)?;
                    Remote::Repair { layer, amount: reader.f64()? }
                }
                11 => {
                    let layer = read_layer(reader)?;
                    let amount = reader.f64()?;
                    let bonus_per_cycle = reader.f64()?;
                    let bonus_max = reader.f64()?;
                    if !amount.is_finite()
                        || amount < 0.0
                        || !bonus_per_cycle.is_finite()
                        || bonus_per_cycle < 0.0
                        || !bonus_max.is_finite()
                        || bonus_max < 0.0
                    {
                        return Err(DecodeError::BadValue("mutadaptive repair"));
                    }
                    Remote::MutadaptiveRepair { layer, amount, bonus_per_cycle, bonus_max }
                }
                1 => Remote::Transfer { amount: reader.f64()? },
                2 => Remote::Neutralize { amount: reader.f64()?, signature_resolution: reader.f64()? },
                3 => Remote::Drain { amount: reader.f64()?, signature_resolution: reader.f64()? },
                4 => Remote::Web { percent: reader.f64()? },
                5 => Remote::Paint { percent: reader.f64()? },
                6 => Remote::Damp {
                    range_percent: reader.f64()?,
                    resolution_percent: reader.f64()?,
                    gravimetric_percent: reader.f64()?,
                    ladar_percent: reader.f64()?,
                    magnetometric_percent: reader.f64()?,
                    radar_percent: reader.f64()?,
                },
                7 => Remote::Scramble {
                    strength: reader.f64()?,
                    blocks_mwd: match reader.u8()? {
                        0 => false,
                        1 => true,
                        _ => return Err(DecodeError::BadValue("blocks_mwd")),
                    },
                },
                12 => {
                    let strength = reader.f64()?;
                    let web_percent = reader.f64()?;
                    if !strength.is_finite() || strength < 0.0 || !web_percent.is_finite() {
                        return Err(DecodeError::BadValue("tackle"));
                    }
                    Remote::Tackle { strength, web_percent }
                }
                9 => Remote::Disrupt {
                    range_percent: reader.f64()?,
                    falloff_percent: reader.f64()?,
                    tracking_percent: reader.f64()?,
                },
                10 => Remote::GuidanceDisrupt {
                    velocity_percent: reader.f64()?,
                    delay_percent: reader.f64()?,
                    aoe_velocity_percent: reader.f64()?,
                    aoe_cloud_percent: reader.f64()?,
                },
                8 => Remote::Jam { strength: [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?] },
                _ => return Err(DecodeError::BadValue("remote")),
            };
            if matches!(&what, Remote::MutadaptiveRepair { .. }) && when != Applies::CycleEnd {
                return Err(DecodeError::BadValue("mutadaptive repair timing"));
            }
            Ok(Action::Remote { what, reach, when, jam })
        }
        2 => {
            let range = reader.f64()?;
            let damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            let hurts_pilots = match reader.u8()? {
                0 => false,
                1 => true,
                _ => return Err(DecodeError::BadValue("hurts_pilots")),
            };
            Ok(Action::Smartbomb { range, damage, hurts_pilots })
        }
        3 => Ok(Action::EcmBurst {
            range: reader.f64()?,
            strength: [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?],
        }),
        5 => Ok(Action::Cloak {
            warp_safe: match reader.u8()? {
                0 => false,
                1 => true,
                _ => return Err(DecodeError::BadValue("warp_safe")),
            },
        }),
        16 => {
            let range = reader.f64()?;
            if !range.is_finite() || range <= 0.0 {
                return Err(DecodeError::BadValue("warp bubble"));
            }
            Ok(Action::WarpBubble { range })
        }
        17 => Ok(Action::WarpImmunity),
        22 => {
            let missile = read_missile(reader)?;
            let damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            if missile.max_velocity < 0.0 || !damage.iter().all(|d| d.is_finite() && *d >= 0.0) {
                return Err(DecodeError::BadValue("defender"));
            }
            Ok(Action::Defender { missile, damage })
        }
        21 => {
            let range = reader.f64()?;
            let duration_ms = reader.f64()?;
            let speed_factor = reader.f64()?;
            if !(range.is_finite()
                && range > 0.0
                && duration_ms.is_finite()
                && duration_ms > 0.0
                && speed_factor.is_finite())
            {
                return Err(DecodeError::BadValue("stasis burst"));
            }
            Ok(Action::StasisBurst { range, duration_ms, speed_factor })
        }
        18 => {
            let delta = reader.f64()?;
            if !delta.is_finite() {
                return Err(DecodeError::BadValue("scramble status"));
            }
            Ok(Action::ScrambleStatus { delta })
        }
        19 => {
            let ball = read_deployable(reader)?;
            let range = reader.f64()?;
            let lifetime_ms = reader.f64()?;
            if !(range.is_finite() && range > 0.0 && lifetime_ms.is_finite() && lifetime_ms > 0.0) {
                return Err(DecodeError::BadValue("warp probe"));
            }
            Ok(Action::WarpProbe { ball, range, lifetime_ms })
        }
        20 => {
            let ball = read_deployable(reader)?;
            let kind = match reader.u8()? {
                0 => BombKind::Damage,
                1 => BombKind::Ecm,
                2 => BombKind::Neutralize,
                _ => return Err(DecodeError::BadValue("bomb kind")),
            };
            let lifetime_ms = reader.f64()?;
            let blast_radius = reader.f64()?;
            let damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            let ecm = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            let neutralize = reader.f64()?;
            let amounts = damage.iter().chain(ecm.iter()).chain([&neutralize]);
            if !(lifetime_ms.is_finite()
                && lifetime_ms > 0.0
                && blast_radius.is_finite()
                && blast_radius > 0.0)
                || amounts.clone().any(|n| !n.is_finite() || *n < 0.0)
            {
                return Err(DecodeError::BadValue("bomb"));
            }
            Ok(Action::Bomb { ball, kind, lifetime_ms, blast_radius, damage, ecm, neutralize })
        }
        29 => {
            // the Nexus invulnerability core (protocol 42): the reach of its fleet effect in metres
            let range_meters = reader.f64()?;
            if !range_meters.is_finite() || range_meters < 0.0 {
                return Err(DecodeError::BadValue("panic action"));
            }
            Ok(Action::Panic { range_meters })
        }
        28 => {
            // a command burst (protocol 42): the reach in metres; its buffs are the host's, read from the charge when it settles
            let range_meters = reader.f64()?;
            if !range_meters.is_finite() || range_meters < 0.0 {
                return Err(DecodeError::BadValue("command burst action"));
            }
            Ok(Action::CommandBurst { range_meters })
        }
        27 => {
            let ball = read_deployable(reader)?;
            let lifetime_ms = reader.f64()?;
            if !(lifetime_ms.is_finite() && lifetime_ms > 0.0) {
                return Err(DecodeError::BadValue("cynosural field"));
            }
            Ok(Action::CynoField { ball, lifetime_ms })
        }
        6 => {
            let distance_meters = reader.f64()?;
            let cooldown_ms = reader.i64()?;
            if !distance_meters.is_finite() || distance_meters <= 0.0 || cooldown_ms <= 0 {
                return Err(DecodeError::BadValue("micro jump drive"));
            }
            Ok(Action::MicroJumpDrive { distance_meters, cooldown_ms })
        }
        12 => {
            let seed = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            let shift = reader.f64()?;
            let attribute_ids = [reader.i32()?, reader.i32()?, reader.i32()?, reader.i32()?];
            if seed.iter().any(|r| !r.is_finite() || !(0.0..=1.0).contains(r))
                || !shift.is_finite()
                || shift <= 0.0
            {
                return Err(DecodeError::BadValue("resistance shift"));
            }
            Ok(Action::ResistanceShift { seed, shift, attribute_ids })
        }
        13 => Ok(Action::Strike { strike: read_strike(reader)? }),
        15 => {
            let strike = read_strike(reader)?;
            let destination = [reader.f64()?, reader.f64()?, reader.f64()?];
            let warp_block_ms = reader.f64()?;
            let moved = read_ids(reader)?;
            let held = read_ids(reader)?;
            if strike.shape != StrikeShape::Sphere
                || !destination.iter().all(|d| d.is_finite())
                || !(warp_block_ms.is_finite() && warp_block_ms >= 0.0)
            {
                return Err(DecodeError::BadValue("transport field"));
            }
            Ok(Action::Field { strike, destination, warp_block_ms, moved, held })
        }
        14 => {
            let delay_ms = reader.f64()?;
            let damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            let immobile_ms = reader.f64()?;
            let no_jump_ms = reader.f64()?;
            let numbers = [delay_ms, immobile_ms, no_jump_ms];
            if !numbers.iter().all(|n| n.is_finite() && *n >= 0.0)
                || !damage.iter().all(|d| d.is_finite() && *d >= 0.0)
            {
                return Err(DecodeError::BadValue("doomsday"));
            }
            Ok(Action::Doomsday { delay_ms, damage, immobile_ms, no_jump_ms })
        }
        23 => {
            let damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            let range = reader.f64()?;
            if !damage.iter().chain([&range]).all(|n| n.is_finite() && *n >= 0.0) {
                return Err(DecodeError::BadValue("kamikaze"));
            }
            Ok(Action::Kamikaze { damage, range })
        }
        25 => {
            let centre = [reader.f64()?, reader.f64()?, reader.f64()?];
            let radius = reader.f64()?;
            let damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            let numbers = centre.iter().copied().chain([radius]).chain(damage);
            if !numbers.clone().all(f64::is_finite) || radius <= 0.0 || damage.iter().any(|d| *d < 0.0) {
                return Err(DecodeError::BadValue("blast"));
            }
            Ok(Action::Blast { centre, radius, damage })
        }
        24 => {
            let damage = [reader.f64()?, reader.f64()?, reader.f64()?, reader.f64()?];
            let reach = reader.f64()?;
            let blast = Blast {
                explosion_radius: reader.f64()?,
                explosion_velocity: reader.f64()?,
                reduction_factor: reader.f64()?,
                reduction_sensitivity: reader.f64()?,
            };
            let numbers = [
                reach,
                blast.explosion_radius,
                blast.explosion_velocity,
                blast.reduction_factor,
                blast.reduction_sensitivity,
            ];
            if !damage.iter().chain(numbers.iter()).all(|n| n.is_finite() && *n >= 0.0) {
                return Err(DecodeError::BadValue("missile"));
            }
            Ok(Action::Missile { damage, reach, blast })
        }
        26 => {
            // a scanner (protocol 40): 0 cargo, 1 ship; the scan's optimal range and its falloff (none for a cargo scan), in metres
            let cargo = match reader.u8()? {
                0 => true,
                1 => false,
                _ => return Err(DecodeError::BadValue("scan kind")),
            };
            let optimal = reader.f64()?;
            let falloff = reader.f64()?;
            if !optimal.is_finite() || optimal <= 0.0 || !falloff.is_finite() || falloff < 0.0 {
                return Err(DecodeError::BadValue("scan action"));
            }
            Ok(Action::Scan { cargo, reach: Reach { optimal, falloff } })
        }
        7 => {
            let family = match reader.u8()? {
                0 => MiningFamily::Ore,
                1 => MiningFamily::Ice,
                2 => MiningFamily::Gas,
                _ => return Err(DecodeError::BadValue("mining family")),
            };
            let mining_amount_m3 = reader.f64()?;
            let waste_multiplier = reader.f64()?;
            let waste_probability = reader.f64()?;
            let crit_probability = reader.f64()?;
            let crit_multiplier = reader.f64()?;
            let numbers = [mining_amount_m3, waste_multiplier, crit_multiplier];
            if mining_amount_m3 <= 0.0
                || numbers.iter().any(|n| !n.is_finite() || *n < 0.0)
                || ![waste_probability, crit_probability]
                    .iter()
                    .all(|p| p.is_finite() && (0.0..=1.0).contains(p))
            {
                return Err(DecodeError::BadValue("mining action"));
            }
            Ok(Action::Mining {
                family,
                mining_amount_m3,
                waste_multiplier,
                waste_probability,
                crit_probability,
                crit_multiplier,
            })
        }
        8 => {
            let access_bonus = reader.f64()?;
            if !access_bonus.is_finite() || access_bonus < 0.0 {
                return Err(DecodeError::BadValue("salvage action"));
            }
            Ok(Action::Salvage { access_bonus })
        }
        9 => {
            let velocity = reader.f64()?;
            let hold = reader.f64()?;
            let range = reader.f64()?;
            if !velocity.is_finite()
                || velocity <= 0.0
                || !hold.is_finite()
                || hold < 0.0
                || !range.is_finite()
                || range <= 0.0
            {
                return Err(DecodeError::BadValue("tractor action"));
            }
            Ok(Action::Tractor { velocity, hold, range })
        }
        10 => Ok(Action::PassiveLock),
        11 => {
            let range = reader.f64()?;
            if !range.is_finite() || range <= 0.0 {
                return Err(DecodeError::BadValue("auto-target action"));
            }
            Ok(Action::AutoTarget { range })
        }
        4 => {
            let charge_type_id = reader.i32()?;
            let charge_flag = reader.i32()?;
            let charge_per_cycle = reader.i64()?;
            let charges = reader.i64()?;
            if charge_per_cycle <= 0 || charges < 0 {
                return Err(DecodeError::BadValue("charged action"));
            }
            let action = Box::new(read_action(reader)?);
            let fallback = match reader.u8()? {
                0 => None,
                1 => Some(Box::new(read_action(reader)?)),
                _ => return Err(DecodeError::BadValue("charged action fallback")),
            };
            Ok(Action::Charged { action, fallback, charge_type_id, charge_flag, charge_per_cycle, charges })
        }
        _ => Err(DecodeError::BadValue("action")),
    }
}
