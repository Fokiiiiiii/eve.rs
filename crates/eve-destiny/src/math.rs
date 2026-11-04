//! JavaScript numeric semantics and V8's `Math.exp`.
//!
//! The reference computes with `Math.*` and `toFiniteNumber`. To reproduce its
//! results bit for bit the port needs the same corner cases (signed zeros,
//! non-finite inputs replaced by fallbacks) and the same `exp`: V8 ships its
//! own fdlibm-derived routine, which differs from the platform libm in the last
//! bit for some arguments, so `f64::exp` cannot be used.

/// `toFiniteNumber(value, fallback)` for a value that is already a number.
#[inline]
pub fn finite_or(value: f64, fallback: f64) -> f64 {
    if value.is_finite() { value } else { fallback }
}

/// `value >>> 0` (ToUint32).
#[inline]
pub fn to_uint32(value: f64) -> u32 {
    if !value.is_finite() {
        return 0;
    }
    value.trunc().rem_euclid(4_294_967_296.0) as u32
}

/// `Math.round`: half rounds up (towards +infinity), so -2.5 becomes -2.
#[inline]
pub fn js_round(value: f64) -> f64 {
    if !value.is_finite() {
        return value;
    }
    let floor = value.floor();
    if value - floor >= 0.5 { floor + 1.0 } else { floor }
}

/// `Math.max(a, b)`: NaN if either is NaN, and +0 beats -0.
#[inline]
pub fn js_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else if a == 0.0 && b == 0.0 {
        if a.is_sign_negative() && b.is_sign_negative() { -0.0 } else { 0.0 }
    } else if a > b {
        a
    } else {
        b
    }
}

/// `Math.exp`, as V8 computes it (a port of FreeBSD msun `e_exp.c`, with V8's
/// exact-`E` special case for `exp(1)`).
pub fn exp(mut x: f64) -> f64 {
    // Constants are built from the fdlibm bit patterns rather than decimal
    // literals, so no rounding can creep in.
    const fn word(high: u32, low: u32) -> f64 {
        f64::from_bits(((high as u64) << 32) | low as u64)
    }
    const HALF: [f64; 2] = [0.5, -0.5];
    const O_THRESHOLD: f64 = word(0x4086_2E42, 0xFEFA_39EF); // 7.09782712893383973096e+02
    const U_THRESHOLD: f64 = word(0xc087_4910, 0xD52D_3051); // -7.45133219101941108420e+02
    const LN2_HI: [f64; 2] = [word(0x3fe6_2e42, 0xfee0_0000), word(0xbfe6_2e42, 0xfee0_0000)];
    const LN2_LO: [f64; 2] = [word(0x3dea_39ef, 0x3579_3c76), word(0xbdea_39ef, 0x3579_3c76)];
    const INV_LN2: f64 = word(0x3ff7_1547, 0x652b_82fe); // 1.44269504088896338700e+00
    const P1: f64 = word(0x3FC5_5555, 0x5555_553E); // 1.66666666666666019037e-01
    const P2: f64 = word(0xBF66_C16C, 0x16BE_BD93); // -2.77777777770155933842e-03
    const P3: f64 = word(0x3F11_566A, 0xAF25_DE2C); // 6.61375632143793436117e-05
    const P4: f64 = word(0xBEBB_BD41, 0xC5D2_6BF1); // -1.65339022054652515390e-06
    const P5: f64 = word(0x3E66_3769, 0x72BE_A4D0); // 4.13813679705723846039e-08
    const E: f64 = word(0x4005_bf0a, 0x8b14_5769); // 2.718281828459045
    const HUGE: f64 = 1.0e300;
    const TWOM1000: f64 = word(0x0170_0000, 0); // 2**-1000
    const TWO1023: f64 = word(0x7fe0_0000, 0); // 2**1023

    let high = (x.to_bits() >> 32) as u32;
    let xsb = ((high >> 31) & 1) as usize; // sign bit of x
    let hx = high & 0x7fff_ffff; // high word of |x|

    let mut hi = 0.0;
    let mut lo = 0.0;
    let mut k: i32 = 0;

    // Filter out non-finite arguments.
    if hx >= 0x4086_2E42 {
        // |x| >= 709.78...
        if hx >= 0x7ff0_0000 {
            let low = x.to_bits() as u32;
            return if ((hx & 0xf_ffff) | low) != 0 {
                x + x // NaN
            } else if xsb == 0 {
                x // exp(+inf) = +inf
            } else {
                0.0 // exp(-inf) = 0
            };
        }
        if x > O_THRESHOLD {
            return HUGE * HUGE; // overflow
        }
        if x < U_THRESHOLD {
            return TWOM1000 * TWOM1000; // underflow
        }
    }

    // Argument reduction.
    if hx > 0x3fd6_2e42 {
        // |x| > 0.5 ln2
        if hx < 0x3ff0_a2b2 {
            // and |x| < 1.5 ln2
            if x == 1.0 {
                return E;
            }
            hi = x - LN2_HI[xsb];
            lo = LN2_LO[xsb];
            k = 1 - xsb as i32 - xsb as i32;
        } else {
            k = (INV_LN2 * x + HALF[xsb]) as i32;
            let t = f64::from(k);
            hi = x - t * LN2_HI[0]; // t * ln2_hi is exact here
            lo = t * LN2_LO[0];
        }
        x = hi - lo;
    } else if hx < 0x3e30_0000 {
        // |x| < 2**-28
        return 1.0 + x;
    }

    // x is now in the primary range.
    let t = x * x;
    let exponent_word = |exponent: i32| -> f64 {
        f64::from_bits(u64::from(0x3ff0_0000i32.wrapping_add(exponent.wrapping_shl(20)) as u32) << 32)
    };
    let twopk = if k >= -1021 { exponent_word(k) } else { exponent_word(k + 1000) };
    let c = x - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
    if k == 0 {
        return 1.0 - ((x * c) / (c - 2.0) - x);
    }
    let y = 1.0 - ((lo - (x * c) / (2.0 - c)) - hi);
    if k >= -1021 {
        if k == 1024 {
            return y * 2.0 * TWO1023;
        }
        y * twopk
    } else {
        y * twopk * TWOM1000
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn js_max_orders_signed_zeros_like_math_max() {
        assert!(js_max(0.0, -0.0).is_sign_positive());
        assert!(js_max(-0.0, 0.0).is_sign_positive());
        assert!(js_max(-0.0, -0.0).is_sign_negative());
        assert!(js_max(1.0, f64::NAN).is_nan());
        assert_eq!(js_max(2.0, 3.0), 3.0);
    }

    #[test]
    fn js_round_matches_math_round() {
        assert_eq!(js_round(2.5), 3.0);
        assert_eq!(js_round(-2.5), -2.0);
        assert_eq!(js_round(0.49999999999999994), 0.0);
        assert_eq!(js_round(-0.2), 0.0);
        assert_eq!(js_round(1e300), 1e300);
        assert!(js_round(f64::NAN).is_nan());
    }

    #[test]
    fn to_uint32_wraps_like_js() {
        assert_eq!(to_uint32(-1.0), 4_294_967_295);
        assert_eq!(to_uint32(4_294_967_296.0), 0);
        assert_eq!(to_uint32(f64::NAN), 0);
        assert_eq!(to_uint32(1.9), 1);
    }

    #[test]
    fn exp_special_values() {
        assert_eq!(exp(0.0), 1.0);
        assert_eq!(exp(1.0), std::f64::consts::E);
        assert_eq!(exp(f64::NEG_INFINITY), 0.0);
        assert_eq!(exp(f64::INFINITY), f64::INFINITY);
        assert!(exp(f64::NAN).is_nan());
        assert_eq!(exp(710.0), f64::INFINITY);
        assert_eq!(exp(-746.0), 0.0);
    }
}
