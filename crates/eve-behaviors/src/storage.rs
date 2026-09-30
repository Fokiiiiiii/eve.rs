//! `behaviors/storage.py`: where the authored behavior trees are kept. The client reads them from the FSD data `behaviortrees.static`
//! (`fsdlite.EveStorage(data='behaviortrees', cache=cache, mapping=MAPPING, indexes=[], coerce=int)`) with an empty mapping, so a tree comes out as
//! the plain dictionary the data holds and `BehaviorTree` below is not what is made of it. The reading of the data is the world's: the storage is the
//! `BehaviorTreeStorage` trait (with or without the cache, `BehaviortreeStorage` and `BehaviortreeStorageWithNoCache`, is the world's choice).

pub use crate::loaders::fsd::{BehaviorTreeStorage, MemoryBehaviorTreeStorage};
use crate::value::Value;

/// `BehaviorTree` of `behaviors/storage.py`: a tree as the storage's data class would hold it.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct BehaviorTreeRecord {
    pub name: Option<Value>,
    pub description: Option<Value>,
    pub root: Option<Value>,
}

impl BehaviorTreeRecord {
    /// `__setstate__(data)`: the fields the class has are set from the data, and what else the data holds is left alone. Data that is not a
    /// dictionary has no `iteritems` (`None` here).
    pub fn from_state(data: &Value) -> Option<Self> {
        let Value::Dict(entries) = data else { return None };
        let mut record = Self::default();
        for (key, value) in entries {
            match key.as_str() {
                Some("name") => record.name = Some(value.clone()),
                Some("description") => record.description = Some(value.clone()),
                Some("root") => record.root = Some(value.clone()),
                _ => {}
            }
        }
        Some(record)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dict(entries: &[(&str, Value)]) -> Value {
        Value::Dict(entries.iter().map(|(k, v)| (Value::from(*k), v.clone())).collect())
    }

    #[test]
    fn a_new_record_has_nothing() {
        let record = BehaviorTreeRecord::default();
        assert_eq!((record.name, record.description, record.root), (None, None, None));
    }

    #[test]
    fn the_state_sets_the_fields_the_record_has_and_ignores_the_rest() {
        let data = dict(&[
            ("name", Value::from("Guard")),
            ("scopeType", Value::from("item")),
            ("root", dict(&[("taskClass", Value::from("Sequence"))])),
            ("parameters", Value::List(Vec::new())),
        ]);
        let record = BehaviorTreeRecord::from_state(&data).unwrap();
        assert_eq!(record.name, Some(Value::from("Guard")));
        assert_eq!(record.description, None);
        assert_eq!(record.root, Some(dict(&[("taskClass", Value::from("Sequence"))])));
        let with_description = dict(&[("description", Value::from("d"))]);
        assert_eq!(
            BehaviorTreeRecord::from_state(&with_description).unwrap().description,
            Some(Value::from("d"))
        );
    }

    #[test]
    fn a_state_that_is_not_a_dictionary_is_not_one() {
        assert_eq!(BehaviorTreeRecord::from_state(&Value::None), None);
        assert_eq!(BehaviorTreeRecord::from_state(&Value::List(Vec::new())), None);
        assert_eq!(
            BehaviorTreeRecord::from_state(&Value::Dict(Vec::new())),
            Some(BehaviorTreeRecord::default())
        );
    }

    #[test]
    fn the_storage_is_the_loaders() {
        let mut storage = MemoryBehaviorTreeStorage::new();
        storage.insert(Value::Int(1), dict(&[("name", Value::from("a"))]));
        assert!(BehaviorTreeStorage::get(&storage, &Value::Int(1)).is_some());
    }
}
