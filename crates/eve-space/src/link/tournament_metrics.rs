//! Bounded Fancy telemetry identities and sample types (SimLink v89).
use super::{DecodeError, Reader, SceneKey};
use std::collections::BTreeSet;
#[derive(Debug, Clone, PartialEq)]
pub struct MetricsIdentity {
    pub request_id: u64,
    pub scene: SceneKey,
    pub match_id: i64,
    pub generation: i64,
    pub start: i64,
    pub roster: Vec<(i64, i64, u8)>,
}
#[derive(Debug, Clone, PartialEq)]
pub struct MetricsFitted {
    pub item: i64,
    pub type_id: i32,
    pub flag: i32,
    pub heat: u8,
}
#[derive(Debug, Clone, PartialEq)]
pub struct MetricsRunning {
    pub item: i64,
    pub effect: i32,
    pub target: i64,
    pub charge: Option<i32>,
    pub rounds: Option<i64>,
}
#[derive(Debug, Clone, PartialEq)]
pub struct MetricsCompanion {
    pub ship: i64,
    pub type_id: i32,
    pub controller: i64,
    pub activity: i32,
    pub kind: u8,
    pub members: Option<i32>,
}
#[derive(Debug, Clone, PartialEq)]
pub struct MetricsPilot {
    pub character: i64,
    pub ship: i64,
    pub team: u8,
    pub state: u8,
    pub fitted: Vec<MetricsFitted>,
    pub running: Vec<MetricsRunning>,
    pub companions: Vec<MetricsCompanion>,
    pub ehp_status: u8,
    pub ehp: Option<f64>,
}
#[derive(Debug, Clone, PartialEq)]
pub struct MetricsSample {
    pub at: i64,
    pub applied: Option<[f64; 2]>,
    pub pilots: Vec<MetricsPilot>,
}
impl MetricsIdentity {
    pub(super) fn valid(&self) -> bool {
        self.request_id > 0
            && self.scene.is_valid()
            && self.scene.instance_id == 0
            && self.match_id > 0
            && self.generation > 0
            && self.start > 0
            && !self.roster.is_empty()
            && self.roster.len() <= 256
            && self.roster.windows(2).all(|w| w[0] < w[1])
            && self.roster.iter().all(|&(c, s, t)| c > 0 && s > 0 && t <= 1)
            && self.roster.iter().map(|r| r.0).collect::<BTreeSet<_>>().len() == self.roster.len()
            && self.roster.iter().map(|r| r.1).collect::<BTreeSet<_>>().len() == self.roster.len()
    }
}
pub(super) fn read_identity(r: &mut Reader) -> Result<MetricsIdentity, DecodeError> {
    let request_id = r.u64()?;
    let scene = SceneKey { location_id: r.i32()?, instance_id: r.i64()?, room_index: r.i32()? };
    let match_id = r.i64()?;
    let generation = r.i64()?;
    let start = r.i64()?;
    let count = r.u16()? as usize;
    if count == 0 || count > 256 {
        return Err(DecodeError::BadValue("metrics roster"));
    }
    let mut roster = Vec::with_capacity(count);
    for _ in 0..count {
        roster.push((r.i64()?, r.i64()?, r.u8()?));
    }
    let id = MetricsIdentity { request_id, scene, match_id, generation, start, roster };
    if !id.valid() {
        return Err(DecodeError::BadValue("metrics identity"));
    }
    Ok(id)
}
pub(super) fn write_identity(out: &mut Vec<u8>, id: &MetricsIdentity) {
    assert!(id.valid());
    out.extend_from_slice(&id.request_id.to_le_bytes());
    out.extend_from_slice(&id.scene.location_id.to_le_bytes());
    out.extend_from_slice(&id.scene.instance_id.to_le_bytes());
    out.extend_from_slice(&id.scene.room_index.to_le_bytes());
    out.extend_from_slice(&id.match_id.to_le_bytes());
    out.extend_from_slice(&id.generation.to_le_bytes());
    out.extend_from_slice(&id.start.to_le_bytes());
    out.extend_from_slice(&(id.roster.len() as u16).to_le_bytes());
    for (c, s, t) in &id.roster {
        out.extend_from_slice(&c.to_le_bytes());
        out.extend_from_slice(&s.to_le_bytes());
        out.push(*t);
    }
}
pub(super) fn valid_status(status: u8, error: &str) -> bool {
    error.len() <= 1024
        && match status {
            0 | 1 => error.is_empty(),
            2 => !error.is_empty(),
            _ => false,
        }
}

fn count(r: &mut Reader, total: &mut usize) -> Result<usize, DecodeError> {
    let n = r.u16()? as usize;
    *total += n;
    if n > 256 || *total > 8192 { Err(DecodeError::BadValue("metrics array")) } else { Ok(n) }
}
fn optional_i32(r: &mut Reader) -> Result<Option<i32>, DecodeError> {
    match r.u8()? {
        0 => Ok(None),
        1 => Ok(Some(r.i32()?)),
        _ => Err(DecodeError::BadValue("metrics option")),
    }
}
fn optional_i64(r: &mut Reader) -> Result<Option<i64>, DecodeError> {
    match r.u8()? {
        0 => Ok(None),
        1 => Ok(Some(r.i64()?)),
        _ => Err(DecodeError::BadValue("metrics option")),
    }
}
pub(super) fn read_sample(
    r: &mut Reader,
    id: &MetricsIdentity,
    v2: bool,
) -> Result<MetricsSample, DecodeError> {
    let at = r.i64()?;
    let applied = match r.u8()? {
        0 => None,
        1 => Some([r.f64()?, r.f64()?]),
        _ => return Err(DecodeError::BadValue("metrics applied")),
    };
    let n = r.u16()? as usize;
    if n != id.roster.len() {
        return Err(DecodeError::BadValue("metrics pilot count"));
    }
    let mut total = 0;
    let mut pilots = Vec::with_capacity(n);
    for &(expected_c, expected_s, expected_t) in &id.roster {
        let character = r.i64()?;
        let ship = r.i64()?;
        let team = r.u8()?;
        let state = r.u8()?;
        if (character, ship, team) != (expected_c, expected_s, expected_t) {
            return Err(DecodeError::BadValue("metrics pilot identity"));
        }
        let mut fitted = Vec::new();
        for _ in 0..count(r, &mut total)? {
            fitted.push(MetricsFitted { item: r.i64()?, type_id: r.i32()?, flag: r.i32()?, heat: r.u8()? });
        }
        let mut running = Vec::new();
        for _ in 0..count(r, &mut total)? {
            running.push(MetricsRunning {
                item: r.i64()?,
                effect: r.i32()?,
                target: r.i64()?,
                charge: optional_i32(r)?,
                rounds: optional_i64(r)?,
            });
        }
        let mut companions = Vec::new();
        for _ in 0..count(r, &mut total)? {
            companions.push(MetricsCompanion {
                ship: r.i64()?,
                type_id: r.i32()?,
                controller: r.i64()?,
                activity: r.i32()?,
                kind: r.u8()?,
                members: if v2 { optional_i32(r)? } else { None },
            });
        }
        let ehp_status = r.u8()?;
        let ehp = if ehp_status == 1 { Some(r.f64()?) } else { None };
        pilots.push(MetricsPilot {
            character,
            ship,
            team,
            state,
            fitted,
            running,
            companions,
            ehp_status,
            ehp,
        });
    }
    let sample = MetricsSample { at, applied, pilots };
    if !valid_sample(&sample, id) {
        return Err(DecodeError::BadValue("metrics sample"));
    }
    Ok(sample)
}
pub(crate) fn valid_sample(sample: &MetricsSample, id: &MetricsIdentity) -> bool {
    if sample.at <= 0
        || sample.pilots.len() != id.roster.len()
        || sample.applied.is_some_and(|a| a.iter().any(|v| !v.is_finite() || *v < 0.0))
    {
        return false;
    }
    let mut total = 0;
    for (p, &ident) in sample.pilots.iter().zip(&id.roster) {
        if (p.character, p.ship, p.team) != ident
            || p.fitted.len() > 256
            || p.running.len() > 256
            || p.companions.len() > 256
        {
            return false;
        }
        if p.state > 1
            || (p.state == 1
                && (!p.fitted.is_empty()
                    || !p.running.is_empty()
                    || !p.companions.is_empty()
                    || p.ehp_status != 1
                    || p.ehp != Some(0.0)))
        {
            return false;
        }
        total += p.fitted.len() + p.running.len() + p.companions.len();
        if p.fitted.iter().map(|m| m.item).collect::<BTreeSet<_>>().len() != p.fitted.len()
            || p.running.iter().map(|m| m.item).collect::<BTreeSet<_>>().len() != p.running.len()
            || p.companions.iter().map(|m| m.ship).collect::<BTreeSet<_>>().len() != p.companions.len()
        {
            return false;
        }
        if p.fitted.iter().any(|m| m.item <= 0 || m.type_id <= 0 || m.heat > 2)
            || p.running.iter().any(|m| {
                m.item <= 0
                    || m.effect <= 0
                    || m.charge.is_some_and(|x| x <= 0)
                    || m.rounds.is_some_and(|x| x < 0)
            })
            || p.companions.iter().any(|c| {
                c.ship <= 0
                    || c.type_id <= 0
                    || c.controller != p.ship
                    || c.kind > 1
                    || c.members.is_some_and(|n| !(0..=64).contains(&n))
                    || (c.kind == 0 && c.members.is_some())
            })
            || !matches!((p.ehp_status, p.ehp), (0, None) | (2, None) | (1, Some(_)))
            || p.ehp.is_some_and(|v| !v.is_finite() || v < 0.0)
        {
            return false;
        }
    }
    total <= 8192
}
pub(super) fn write_sample(out: &mut Vec<u8>, s: &MetricsSample, id: &MetricsIdentity, v2: bool) {
    assert!(valid_sample(s, id));
    out.extend_from_slice(&s.at.to_le_bytes());
    out.push(u8::from(s.applied.is_some()));
    if let Some(a) = s.applied {
        for v in a {
            out.extend_from_slice(&v.to_le_bytes());
        }
    }
    out.extend_from_slice(&(s.pilots.len() as u16).to_le_bytes());
    for p in &s.pilots {
        out.extend_from_slice(&p.character.to_le_bytes());
        out.extend_from_slice(&p.ship.to_le_bytes());
        out.push(p.team);
        out.push(p.state);
        out.extend_from_slice(&(p.fitted.len() as u16).to_le_bytes());
        for m in &p.fitted {
            out.extend_from_slice(&m.item.to_le_bytes());
            out.extend_from_slice(&m.type_id.to_le_bytes());
            out.extend_from_slice(&m.flag.to_le_bytes());
            out.push(m.heat);
        }
        out.extend_from_slice(&(p.running.len() as u16).to_le_bytes());
        for m in &p.running {
            out.extend_from_slice(&m.item.to_le_bytes());
            out.extend_from_slice(&m.effect.to_le_bytes());
            out.extend_from_slice(&m.target.to_le_bytes());
            out.push(u8::from(m.charge.is_some()));
            if let Some(v) = m.charge {
                out.extend_from_slice(&v.to_le_bytes());
            }
            out.push(u8::from(m.rounds.is_some()));
            if let Some(v) = m.rounds {
                out.extend_from_slice(&v.to_le_bytes());
            }
        }
        out.extend_from_slice(&(p.companions.len() as u16).to_le_bytes());
        for c in &p.companions {
            out.extend_from_slice(&c.ship.to_le_bytes());
            out.extend_from_slice(&c.type_id.to_le_bytes());
            out.extend_from_slice(&c.controller.to_le_bytes());
            out.extend_from_slice(&c.activity.to_le_bytes());
            out.push(c.kind);
            if v2 {
                out.push(u8::from(c.members.is_some()));
                if let Some(members) = c.members {
                    out.extend_from_slice(&members.to_le_bytes());
                }
            }
        }
        out.push(p.ehp_status);
        if let Some(v) = p.ehp {
            out.extend_from_slice(&v.to_le_bytes());
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::link::Message;
    #[test]
    fn metrics_frames_round_trip_and_reject_invalid_identity_and_values() {
        let id = MetricsIdentity {
            request_id: 1,
            scene: SceneKey::solar_system(30_000_001),
            match_id: 2,
            generation: 3,
            start: 4,
            roster: vec![(10, 20, 0), (11, 21, 1)],
        };
        let sample = MetricsSample {
            at: 10,
            applied: Some([12.0, 0.0]),
            pilots: id
                .roster
                .iter()
                .map(|&(character, ship, team)| MetricsPilot {
                    character,
                    ship,
                    team,
                    state: 0,
                    fitted: vec![MetricsFitted { item: 30, type_id: 31, flag: 32, heat: 0 }],
                    running: vec![MetricsRunning {
                        item: 30,
                        effect: 5,
                        target: 21,
                        charge: Some(33),
                        rounds: Some(4),
                    }],
                    companions: vec![],
                    ehp_status: 1,
                    ehp: Some(100.0),
                })
                .collect(),
        };
        let messages = [
            Message::RegisterTournamentMetrics { identity: id.clone() },
            Message::RegisterTournamentMetricsReply { identity: id.clone(), status: 0, error: String::new() },
            Message::ReadTournamentMetrics { identity: id.clone() },
            Message::ReadTournamentMetricsReply {
                identity: id.clone(),
                status: 0,
                error: String::new(),
                sample: Some(sample.clone()),
            },
            Message::ReadTournamentMetricsReply {
                identity: id.clone(),
                status: 1,
                error: String::new(),
                sample: None,
            },
            Message::ReleaseTournamentMetrics { identity: id.clone() },
            Message::ReleaseTournamentMetricsReply { identity: id.clone(), status: 0, error: String::new() },
        ];
        for message in messages {
            let frame = message.encode();
            assert_eq!(Message::decode(&frame[4..]).unwrap(), message);
            let mut bad = frame[4..].to_vec();
            bad.push(0);
            assert!(Message::decode(&bad).is_err());
        }
        let mut bad = sample.clone();
        bad.applied = Some([f64::NAN, 0.0]);
        assert!(!valid_sample(&bad, &id));
        bad = sample.clone();
        bad.pilots[0].fitted[0].heat = 3;
        assert!(!valid_sample(&bad, &id));
        bad = sample;
        bad.pilots[0].ship = 999;
        assert!(!valid_sample(&bad, &id));
        let mut bad_id = id.clone();
        bad_id.roster.reverse();
        assert!(!bad_id.valid());
        assert!(!valid_status(2, ""));
    }
}
