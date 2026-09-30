//! Macros that make the client's small tasks (a class with one `OnEnter`) short.

/// A task that is decided when it is entered: `enter_task!(Name, |t| { ... })` is a unit struct `Name` whose `on_enter` is the block, with the
/// task's view of everything as `t`. The client's conditions and many of its one-step actions are of this kind; they set their own status.
macro_rules! enter_task {
    ($(#[$meta:meta])* $name:ident, |$t:ident| $body:block) => {
        $(#[$meta])*
        #[derive(Debug, Default)]
        pub struct $name;

        impl $crate::tree::Task for $name {
            fn type_name(&self) -> &'static str {
                stringify!($name)
            }

            fn on_enter(&mut self, $t: &mut $crate::tree::TaskCtx<'_, '_>) $body
        }
    };
}
