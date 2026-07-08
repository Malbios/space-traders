namespace SpaceKids.Core.Dsl

/// The internal DSL (§10). Blockly workspace JSON compiles into this; the F# server
/// validates and executes it. Structurally mirrors §10's JSON example (field names
/// kept close in spirit) without a JSON serializer yet — nothing persists this JSON
/// until a milestone that actually reads/writes `programs.compiled_dsl_json`.

type LiteralValue =
    | StringLit of string
    | NumberLit of float
    | BoolLit of bool

/// Blockly `lists_getIndex` / `lists_setIndex` WHERE modes (FROM_START is the
/// default when the field is absent).
type ListIndexWhere =
    | FromStart
    | FromEnd
    | First
    | Last
    | Random

/// Pure expression tree (§10: "inline arguments must be pure"). Never itself performs
/// an API call or a custom-block call — those are hoisted into instructions by the
/// compiler (see Compiler.fs) and referenced back here as a TempRef.
type Expr =
    | Literal of LiteralValue
    | VariableRef of name: string
    | ParamRef of name: string
    | TempRef of name: string
    | Accessor of field: string * target: Expr
    | Arithmetic of op: string * left: Expr * right: Expr
    | Comparison of op: string * left: Expr * right: Expr
    | LogicalOp of op: string * left: Expr * right: Expr
    | LogicalNot of operand: Expr
    | ListLiteral of items: Expr list
    | ListGet of list: Expr * where: ListIndexWhere * index: Expr option
    /// A custom block's structured-output "build record" block (§9 Outputs,
    /// Milestone 9/Part C) — a flat `VRecord` literal, symmetric with the fixed §8
    /// records produced by info blocks. Only ever appears plugged into a definition
    /// block's return-value socket.
    | RecordLiteral of fields: (string * Expr) list

type WhileMode =
    | While
    | Until

/// One instruction. `blockId` is the originating Blockly block id, used to highlight
/// the currently-running block (§10).
type Instruction =
    /// A SpaceTraders action block (navigate, dock, buyGood, ...) with no return value.
    | ApiAction of blockId: string * actionType: string * args: Map<string, Expr>
    /// A SpaceTraders information block (getFuel, getMarket, ...), hoisted per §10
    /// because it's effectful even though it reads on the canvas as a value block.
    | InfoRead of blockId: string * infoType: string * args: Map<string, Expr> * resultTarget: string
    | ShowMessage of blockId: string * text: Expr
    | Wait of blockId: string * seconds: Expr
    | SetVariable of blockId: string * name: string * value: Expr
    | ChangeVariable of blockId: string * name: string * delta: Expr
    /// Blockly `lists_setIndex` (SET mode): mutates a list variable in place.
    | ListSet of
        blockId: string
        *
        name: string
        *
        where: ListIndexWhere
        *
        index: Expr option
        *
        value: Expr
    | If of blockId: string * branches: (Expr * Instruction list) list * elseBranch: Instruction list option
    | Repeat of blockId: string * count: Expr * body: Instruction list
    | WhileUntil of blockId: string * mode: WhileMode * condition: Expr * body: Instruction list
    | ForEach of blockId: string * variable: string * list: Expr * body: Instruction list
    /// Flotilla F1: runs `body` with a runtime-selected "current ship". If the
    /// evaluated ship reference cannot be resolved/acquired, `elseBranch` runs when
    /// present; otherwise the job fails.
    | WithShip of blockId: string * ship: Expr * body: Instruction list * elseBranch: Instruction list option
    /// Flotilla F2: runs each branch concurrently and joins once every branch settles.
    | Parallel of blockId: string * branches: Instruction list list
    /// A custom-block call (§9d) — a real call, not inlined. `resultTarget` is set only
    /// when the referenced block's signature has an output.
    | CallCustomBlock of blockId: string * customBlockId: string * arguments: Map<string, Expr> * resultTarget: string option
    /// Exits the nearest enclosing `Repeat`/`WhileUntil`/`ForEach` immediately.
    | Break of blockId: string
    /// Skips straight to the nearest enclosing loop's next iteration (or exits it, if
    /// the current iteration was its last).
    | Continue of blockId: string
    /// Internal scheduler marker appended by the compiler to a `WithShip` body.
    | ExitShipScope of blockId: string

type CustomBlockSignatureInput = { name: string; inputType: string }

type CustomBlockSignature =
    { inputs: CustomBlockSignatureInput list
      output: string option
      /// `Some fieldNames` when `output` is a structured record built by this
      /// block's own `sk_build_record` return value (§9 Outputs, Milestone 9/Part
      /// C) — `None` for a plain-value or void output. Field order matches
      /// declaration order in the mutator, mirrored by the client's dynamically
      /// generated `accessor_<id>_<field>` blocks (kept in sync manually, same as
      /// the fixed §8 `GENERIC_ACCESSOR_TYPES` set).
      outputFields: string list option }

/// A custom block's definition, as looked up from storage (§9's `custom_blocks`/
/// `custom_block_versions` tables — Milestone 9 scope; Milestone 4 only needs the
/// shape, supplied via a lookup function so Core stays free of persistence concerns).
type CustomBlockDefinition =
    { id: string
      signature: CustomBlockSignature
      /// The block's own body, as raw Blockly workspace JSON (its Blockwerkstatt) —
      /// compiled recursively by Compiler.fs, same as the main program.
      workspaceJson: string }

/// One referenced custom block's compiled body plus the signature snapshot it was
/// compiled against (§9 mismatch check, §10 "recorded once per custom block").
/// `returnExpr` is `None` for a block with no output (`signature.output = None`);
/// evaluated against the callee's own locals when its frame pops (§9d, §14).
type CompiledCustomBlock =
    { signature: CustomBlockSignature
      instructions: Instruction list
      returnExpr: Expr option }

type CompiledProgram =
    { version: int
      customBlocks: Map<string, CompiledCustomBlock>
      instructions: Instruction list }

/// A compile-time or validation-time problem — user-facing text in whichever
/// `Locale` the caller asked for (Milestone 12, bilingual support).
type DslError = { blockId: string option; message: string }

/// Milestone 12 (bilingual support): `Validator.fs`'s messages are the only DSL-level
/// text this covers so far — `Compiler.fs`'s own compile-time errors are still
/// German-only (a known, documented follow-up gap, not part of this milestone's scope).
type Locale =
    | De
    | En

module Locale =
    /// `"de"`/`"en"` (anything else falls back to German) — matches the stored
    /// server-side setting's raw string representation (`SettingsRepository`).
    let ofString (s: string) : Locale = if s = "en" then En else De
