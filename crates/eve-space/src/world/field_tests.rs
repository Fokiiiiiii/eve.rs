use super::*;

#[test]
fn capital_size_is_loaded_from_type_dogma_attribute_1785() {
    let Some(sde) = crate::sde::Sde::locate() else { return };
    let world = World::load(sde).expect("the located SDE loads");

    assert_eq!(world.field_capital_size_by_type.get(&11_567), Some(&true), "Avatar is capital-sized");
    assert_eq!(world.field_capital_size_by_type.get(&587), Some(&false), "Rifter is not capital-sized");
}
