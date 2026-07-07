/// Compiled first in every test project — must not `open` any SpaceKids.Server
/// module, because F# processes all `open` directives before any `do` bindings.
module SpaceKids.TestBootstrap

open SpaceKids.TestSupport

do TestDbGuard.EnsureInitialized |> ignore