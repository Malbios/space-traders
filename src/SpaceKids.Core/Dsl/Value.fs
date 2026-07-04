namespace SpaceKids.Core.Dsl

/// Runtime value produced by evaluating a pure `Expr` (§10). Added in Milestone 6
/// alongside the scheduler — the compiler/validator never needed a runtime
/// representation before now, only the static `Expr`/`LiteralValue` shapes.
type Value =
    | VNumber of float
    | VString of string
    | VBool of bool
    | VList of Value list
    /// Milestone 9/Part B (§8): a "friendly structured record" — Schiff/Fracht/Werft/
    /// Markt/Auftrag/Wegpunkt — produced by an info-read block and consumed only via
    /// `Accessor`. Kept flat per §8's own instruction, not a general nested object.
    | VRecord of Map<string, Value>
