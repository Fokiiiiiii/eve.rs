//! `behaviors/loaders/*`: how a behavior tree is made from its id: from the authored data (`fsd`), or from the code that builds it (`scripted`).

use crate::registry::{ConvertError, RegistryError};

pub mod fsd;
pub mod scripted;

/// What a loader raises (`behaviors/loaders/__init__.py` and what the loaders themselves raise).
#[derive(Debug, Clone, PartialEq)]
pub enum LoaderError {
    /// `BehaviorDoesNotExistError`.
    BehaviorDoesNotExist,
    /// `IncompatibleScopeTypeError`: the tree is for another kind of blackboard than the one it is loaded for.
    IncompatibleScopeType,
    /// `TaskAttributeMissingError`: a task of the data has not an attribute that its class needs (and that is not optional).
    TaskAttributeMissing { task: String, attribute: String },
    /// `CircularReferenceError`: a tree that refers to itself through its subtrees.
    CircularReference(String),
    /// A value of the data that is not what the attribute takes.
    Convert { task: String, attribute: String, error: ConvertError },
    /// A class that is not registered.
    Registry(RegistryError),
    /// Data that is not what a behavior tree is made of (the client would raise a `KeyError` or a `TypeError`).
    Malformed(String),
}

impl From<RegistryError> for LoaderError {
    fn from(error: RegistryError) -> Self {
        LoaderError::Registry(error)
    }
}
