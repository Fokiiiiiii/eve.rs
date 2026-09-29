//! How the game names what is in space.

/// The Roman numeral of a planet's number (1 is I). Numbers below 1 are written as themselves.
pub fn roman(number: i32) -> String {
    if number <= 0 {
        return number.to_string();
    }
    const TABLE: [(i32, &str); 13] = [
        (1000, "M"),
        (900, "CM"),
        (500, "D"),
        (400, "CD"),
        (100, "C"),
        (90, "XC"),
        (50, "L"),
        (40, "XL"),
        (10, "X"),
        (9, "IX"),
        (5, "V"),
        (4, "IV"),
        (1, "I"),
    ];
    let mut left = number;
    let mut text = String::new();
    for (value, symbol) in TABLE {
        while left >= value {
            text.push_str(symbol);
            left -= value;
        }
    }
    text
}

/// A planet: the system and its number (`Manifest IV`).
pub fn planet(system: &str, celestial_index: i32) -> String {
    format!("{system} {}", roman(celestial_index))
}

/// A moon: its planet and its number (`Manifest IV - Moon 2`).
pub fn moon(system: &str, planet_index: i32, moon_index: i32) -> String {
    format!("{} - Moon {moon_index}", planet(system, planet_index))
}

/// An asteroid belt (`Manifest IV - Asteroid Belt 1`).
pub fn belt(system: &str, planet_index: i32, belt_index: i32) -> String {
    format!("{} - Asteroid Belt {belt_index}", planet(system, planet_index))
}

/// The star (`Manifest - Star`).
pub fn star(system: &str) -> String {
    format!("{system} - Star")
}

/// A stargate, named for where it leads (`Stargate (Yulai)`).
pub fn stargate(destination_system: &str) -> String {
    format!("Stargate ({destination_system})")
}

/// A station: where it is, whose it is, and (for most) what kind of station it is.
pub fn station(
    system: &str,
    celestial_index: i32,
    moon_index: Option<i32>,
    owner: &str,
    operation: Option<&str>,
) -> String {
    let place = match moon_index {
        Some(moon_index) => moon(system, celestial_index, moon_index),
        None => planet(system, celestial_index),
    };
    match operation {
        Some(operation) if !operation.is_empty() => format!("{place} - {owner} {operation}"),
        _ => format!("{place} - {owner}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn numbers_are_written_in_roman() {
        assert_eq!(roman(1), "I");
        assert_eq!(roman(4), "IV");
        assert_eq!(roman(9), "IX");
        assert_eq!(roman(14), "XIV");
        assert_eq!(roman(1994), "MCMXCIV");
        assert_eq!(roman(0), "0");
        assert_eq!(roman(-3), "-3");
    }

    #[test]
    fn celestials_are_named_as_the_reference_names_them() {
        assert_eq!(planet("Manifest", 4), "Manifest IV");
        assert_eq!(moon("Manifest", 7, 12), "Manifest VII - Moon 12");
        assert_eq!(belt("Manifest", 5, 1), "Manifest V - Asteroid Belt 1");
        assert_eq!(star("Manifest"), "Manifest - Star");
        assert_eq!(stargate("Yulai"), "Stargate (Yulai)");
    }

    #[test]
    fn a_station_is_named_for_its_place_owner_and_operation() {
        assert_eq!(
            station("Manifest", 5, None, "AIR Laboratories", Some("Trade Center")),
            "Manifest V - AIR Laboratories Trade Center"
        );
        assert_eq!(
            station("Manifest", 4, Some(5), "Paragon", Some("Fulfillment Center")),
            "Manifest IV - Moon 5 - Paragon Fulfillment Center"
        );
        assert_eq!(station("Manifest", 6, None, "CONCORD Bureau", None), "Manifest VI - CONCORD Bureau");
        assert_eq!(station("Manifest", 6, None, "CONCORD", Some("")), "Manifest VI - CONCORD");
    }
}
