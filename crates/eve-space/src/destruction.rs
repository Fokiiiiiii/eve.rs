//! The effects the client plays where a ball ends (`TerminalPlayDestructionEffect(id, effect)`): a port, as it is, of `destructionEffect/destructionType.py` of the
//! client (V24.01, `Fokiiiiiii/eve-logic`). A ship blows up (`EXPLOSION`), an asteroid that is mined out dissolves (`DISSOLVE`, which the client takes 33 seconds
//! over), and a pilot who is thrown out of a ship is shown the reverse (`REVERSE_EJECT`, 11 seconds).
/// No effect: the ball is gone at once.
pub const NONE: i64 = 0;
/// The explosion of a ship or a missile, whatever the ball would have played.
pub const EXPLOSION_OVERRIDE: i64 = 1;
/// A ball that is taken apart slowly (a mined-out asteroid).
pub const DISSOLVE: i64 = 2;
/// The explosion.
pub const EXPLOSION: i64 = 3;
/// The pilot leaves the ship: played backwards.
pub const REVERSE_EJECT: i64 = 4;

/// How long the client takes to play an effect, in milliseconds, for the effects that take a known time (`DURATION_BY_DESTRUCTION_TYPE`).
pub fn duration_ms(effect: i64) -> Option<i64> {
    match effect {
        DISSOLVE => Some(33_000),
        REVERSE_EJECT => Some(11_000),
        _ => None,
    }
}

/// `isExplosionOrOverride`.
pub fn is_explosion_or_override(effect: i64) -> bool {
    effect == EXPLOSION || effect == EXPLOSION_OVERRIDE
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_effects_have_the_numbers_the_client_gives_them() {
        assert_eq!([NONE, EXPLOSION_OVERRIDE, DISSOLVE, EXPLOSION, REVERSE_EJECT], [0, 1, 2, 3, 4]);
    }

    #[test]
    fn only_the_dissolve_and_the_reverse_eject_have_a_length_and_only_the_two_explosions_are_explosions() {
        assert_eq!(duration_ms(DISSOLVE), Some(33_000));
        assert_eq!(duration_ms(REVERSE_EJECT), Some(11_000));
        assert_eq!([NONE, EXPLOSION_OVERRIDE, EXPLOSION].map(duration_ms), [None, None, None]);
        assert_eq!(
            [NONE, EXPLOSION_OVERRIDE, DISSOLVE, EXPLOSION, REVERSE_EJECT].map(is_explosion_or_override),
            [false, true, false, true, false]
        );
    }
}
