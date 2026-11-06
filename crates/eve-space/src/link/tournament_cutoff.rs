//! Validation shared by tournament cutoff request and reply codecs.
use super::{DecodeError, Reader, SceneKey};
use std::collections::HashSet;

pub(super) const MAX_ROSTER: usize = 256;

#[derive(Debug, Clone, PartialEq)]
pub struct CutoffIdentity {
    pub request_id: u64,
    pub scene: SceneKey,
    pub match_id: i64,
    pub generation: i64,
    pub start: i64,
    pub deadline: i64,
}

impl CutoffIdentity {
    pub(super) fn valid(&self) -> bool {
        self.request_id > 0
            && self.scene.is_valid()
            && self.scene.instance_id == 0
            && self.match_id > 0
            && self.generation > 0
            && self.start > 0
            && self.deadline > self.start
    }
}

pub(super) fn valid_roster(roster: &[(i64, i64)]) -> bool {
    if roster.is_empty() || roster.len() > MAX_ROSTER {
        return false;
    }
    let mut characters = HashSet::with_capacity(roster.len());
    let mut ships = HashSet::with_capacity(roster.len());
    roster.iter().all(|&(character, ship)| {
        character > 0 && ship > 0 && characters.insert(character) && ships.insert(ship)
    })
}

pub(super) fn valid_status(status: u8, error: &str) -> bool {
    match status {
        0..=2 => error.is_empty(),
        3 => !error.is_empty(),
        _ => false,
    }
}

pub(super) fn read_identity(r: &mut Reader) -> Result<CutoffIdentity, DecodeError> {
    let request_id = r.u64()?;
    let scene = SceneKey { location_id: r.i32()?, instance_id: r.i64()?, room_index: r.i32()? };
    let value = CutoffIdentity {
        request_id,
        scene,
        match_id: r.i64()?,
        generation: r.i64()?,
        start: r.i64()?,
        deadline: r.i64()?,
    };
    if !value.valid() {
        return Err(DecodeError::BadValue("tournament cutoff identity"));
    }
    Ok(value)
}

pub(super) fn write_identity(out: &mut Vec<u8>, identity: &CutoffIdentity) {
    assert!(identity.valid());
    out.extend_from_slice(&identity.request_id.to_le_bytes());
    out.extend_from_slice(&identity.scene.location_id.to_le_bytes());
    out.extend_from_slice(&identity.scene.instance_id.to_le_bytes());
    out.extend_from_slice(&identity.scene.room_index.to_le_bytes());
    out.extend_from_slice(&identity.match_id.to_le_bytes());
    out.extend_from_slice(&identity.generation.to_le_bytes());
    out.extend_from_slice(&identity.start.to_le_bytes());
    out.extend_from_slice(&identity.deadline.to_le_bytes());
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cutoff_frames_round_trip_and_reject_trailing_bytes() {
        use super::super::Message;
        let identity = CutoffIdentity {
            request_id: 7,
            scene: SceneKey::solar_system(30_000_001),
            match_id: 8,
            generation: 9,
            start: 10,
            deadline: 20,
        };
        let messages = [
            Message::RegisterTournamentCutoff { identity: identity.clone(), roster: vec![(1, 2), (3, 4)] },
            Message::RegisterTournamentCutoffReply {
                identity: identity.clone(),
                status: 0,
                error: String::new(),
            },
            Message::ReadTournamentCutoff { identity: identity.clone() },
            Message::ReadTournamentCutoffReply {
                identity: identity.clone(),
                status: 1,
                evidence: vec![(1, 2, 0), (3, 4, 1)],
                error: String::new(),
            },
            Message::ReleaseTournamentCutoff { identity: identity.clone() },
            Message::ReleaseTournamentCutoffReply { identity, status: 3, error: "conflict".into() },
        ];
        for message in messages {
            let mut payload = message.payload();
            assert_eq!(Message::decode(&payload).unwrap(), message);
            payload.push(0);
            assert!(Message::decode(&payload).is_err());
        }
    }
    #[test]
    fn roster_and_status_reject_ambiguous_wire_values() {
        assert!(valid_roster(&[(1, 2), (3, 4)]));
        assert!(!valid_roster(&[]));
        assert!(!valid_roster(&[(1, 2), (1, 3)]));
        assert!(!valid_roster(&[(1, 2), (3, 2)]));
        assert!(!valid_roster(&[(0, 2)]));
        assert!(!valid_roster(&vec![(1, 2); 257]));
        assert!(valid_status(0, ""));
        assert!(valid_status(3, "failed"));
        assert!(!valid_status(1, "failed"));
        assert!(!valid_status(3, ""));
        assert!(!valid_status(4, ""));
    }
}
