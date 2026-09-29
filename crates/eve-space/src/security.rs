//! What a system's security status looks like to the client: its class, the icon on a stargate, the warning before a step down.

fn pseudo(security: f64) -> f64 {
    let clamped = if security.is_finite() { security.clamp(0.0, 1.0) } else { 0.0 };
    if clamped > 0.0 && clamped < 0.05 { 0.05 } else { clamped }
}

/// 0 null security, 1 low security, 2 high security.
pub fn class(security: f64) -> u8 {
    let security = pseudo(security);
    if security <= 0.0 {
        0
    } else if security < 0.45 {
        1
    } else {
        2
    }
}

/// `SEC_1_0`, `SEC_0_9`, ...: the security to one decimal, as the client names its icons.
pub fn icon_key(security: f64) -> String {
    let tenths = (pseudo(security) * 10.0).round().clamp(0.0, 10.0) as i32;
    format!("SEC_{}_{}", tenths / 10, tenths % 10)
}

/// Whether a jump from one system to the other takes the pilot to less safe space (high to low or null, low to null).
pub fn hazardous_transition(from: f64, to: f64) -> bool {
    let (from, to) = (class(from), class(to));
    (from == 2 && to != 2) || (from == 1 && to == 0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn classes_follow_the_bands() {
        assert_eq!(
            [class(1.0), class(0.45), class(0.44), class(0.01), class(0.0), class(-0.5)],
            [2, 2, 1, 1, 0, 0]
        );
    }

    #[test]
    fn icons_name_the_security_to_one_decimal() {
        assert_eq!(icon_key(1.0), "SEC_1_0");
        assert_eq!(icon_key(0.949), "SEC_0_9");
        assert_eq!(icon_key(0.04), "SEC_0_1");
        assert_eq!(icon_key(-1.0), "SEC_0_0");
    }

    #[test]
    fn only_a_step_down_is_hazardous() {
        assert!(hazardous_transition(0.9, 0.3));
        assert!(hazardous_transition(0.9, -0.1));
        assert!(hazardous_transition(0.3, -0.1));
        assert!(!hazardous_transition(0.3, 0.9));
        assert!(!hazardous_transition(0.9, 0.5));
        assert!(!hazardous_transition(-0.1, -0.5));
    }
}
