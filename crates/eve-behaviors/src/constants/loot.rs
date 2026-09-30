//! `behaviors/const/loot.py`: the loot tables an NPC group of a faction adds to what it drops.

/// `EXTRA_LOOT_TABLES_BY_OWNER_ID_AND_ENTITY_GROUP_ID`, as `(owner id, entity group id, loot table id)`: every entry of the client's table has one loot
/// table.
pub const EXTRA_LOOT_TABLES_BY_OWNER_ID_AND_ENTITY_GROUP_ID: [(i64, i64, i64); 17] = [
    (1000069, 1767, 10201),
    (1000074, 1767, 10200),
    (1000070, 1767, 10199),
    (1000006, 1767, 10198),
    (1000007, 1767, 10197),
    (1000008, 1767, 10196),
    (1000098, 1767, 10195),
    (1000097, 1767, 10202),
    (1000103, 1767, 10203),
    (1000055, 1767, 10204),
    (1000048, 1767, 10205),
    (1000160, 1767, 10206),
    (1000161, 1767, 10207),
    (1000134, 1767, 10208),
    (1000141, 1767, 10210),
    (1000133, 1767, 10209),
    (1000298, 4028, 12082),
];

/// `EXTRA_LOOT_TABLES_BY_OWNER_ID_AND_ENTITY_GROUP_ID.get(owner_id)`: `{entity group id: [loot table ids]}`.
pub fn extra_loot_tables_for_owner(owner_id: i64) -> Option<Vec<(i64, Vec<i64>)>> {
    let mut groups: Vec<(i64, Vec<i64>)> = Vec::new();
    for (owner, group, table) in EXTRA_LOOT_TABLES_BY_OWNER_ID_AND_ENTITY_GROUP_ID {
        if owner != owner_id {
            continue;
        }
        match groups.iter_mut().find(|(g, _)| *g == group) {
            Some((_, tables)) => tables.push(table),
            None => groups.push((group, vec![table])),
        }
    }
    if groups.is_empty() { None } else { Some(groups) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_owner_has_its_tables_and_others_have_none() {
        assert_eq!(extra_loot_tables_for_owner(1000298), Some(vec![(4028, vec![12082])]));
        assert_eq!(extra_loot_tables_for_owner(1000069), Some(vec![(1767, vec![10201])]));
        assert_eq!(extra_loot_tables_for_owner(5), None);
    }
}
