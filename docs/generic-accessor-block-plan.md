# Collapse the 9 record-field accessor blocks into one connection-aware block

## Status: planned, not yet implemented

## Context

Today, reading a field off a structured record (a ship, cargo, market, waypoint,
...) means picking one of 9 separate Blockly block types from `RECORD_FIELD_BLOCKS`
(`src/SpaceKids.Client/Blockly/blocks-catalog.ts:533-673`) — `shipField`,
`cargoField`, `goodField`, `shipyardField`, `shipyardTypeField`, `marketField`,
`tradeGoodField`, `contractField`, `waypointField`, plus 9 more added later
(`agentField`, `systemField`, `factionField`, `factionReputationField`,
`jumpGateField`, `constructionField`, `constructionMaterialField`, `navField`,
`cooldownField`, `priceField`, `moduleField`, `mountField`, `supplyChainField` — 21
total). Each is registered by `registerRecordFieldBlock` with its own fixed `TARGET`
check type and its own fixed `FIELD` dropdown option list. They already replaced an
even older one-block-per-field scheme (29 types) in the same "collapse for
discoverability" spirit this request continues — see `LEGACY_ACCESSOR_BLOCKS`
just below them in the same file for that history.

The user wants to go one step further: a single block whose `FIELD` dropdown shows
only the properties that make sense for whatever's actually plugged into `TARGET`,
rather than making the child pick the right block type up front.

## Why the compiler side is already a non-issue

`Compiler.fs`'s `GENERIC_ACCESSOR_TYPES` set already treats all `RECORD_FIELD_BLOCKS`
types identically: every one compiles to the exact same `Accessor(field, target)`
expression node, where `field` is just whatever string the block's `FIELD` dropdown
holds — the compiler never branches on which of the 9 block *types* it saw. Same for
`Validator.fs`, which only ever sees the already-compiled `Accessor` node, never the
raw block type. So collapsing block types on the client is a pure Blockly/TS-layer
change; **no F# compiler or validator change is needed beyond registering the one new
block type name into the existing generic-accessor matching**, exactly the same
one-line addition `LEGACY_ACCESSOR_FIELD_NAMES`/`GENERIC_ACCESSOR_TYPES` already show
as the established pattern.

At runtime, `Eval.fs`'s `Accessor` evaluation does a plain `Map.tryFind field` against
whatever `VRecord` the target expression produced — field lookup is purely
name-based, not tied to which UI concept ("ship", "cargo", ...) picked that name. That
matters a lot for the design below, because it means **merging field names from
different record shapes into one dropdown is always compiler/runtime-safe**: if the
selected field happens to not exist on the actual value at runtime, it fails the same
way an ill-typed accessor already can today (see the `Bug?` investigation this
session: `Eval.asString`/`asFloat`/etc. already reject the wrong `Value` shape with a
clear German message) — it doesn't produce silently wrong data.

## The real constraint: Blockly variables carry no type information

The one place true dynamism breaks down: **stock Blockly `variables_get`/
`variables_set` have no connection check at all** (already documented at
`blocks-catalog.ts:991-1001`, discovered while fixing `controls_forEach`'s missing
`"List"` check). In practice, most real programs read a record via an info block
(`getShipInfo`, `getMarket`, ...), immediately store it in a variable (`merke ...
als`), and access fields off the *variable* later — often across a loop or multiple
statements — not off a directly-wired info block. When the thing plugged into
`TARGET` is a bare `variables_get`, Blockly's connection-check system has never
tracked what that variable "is", so **there is no reliable way to narrow the dropdown
to just the right fields in that case** — this isn't a SpaceKids gap, it's inherent
to how stock Blockly variables work.

So the honest feasibility verdict is:

- **Direct-wire case** (an info block, or a custom block's structured-output
  accessor, plugged straight into `TARGET`): fully dynamic narrowing is achievable —
  Blockly lets a block inspect what's connected to one of its own inputs.
  synchronously, in the `FIELD` dropdown's own option-generator.
- **Variable-mediated case** (the far more common one in practice): falls back to one
  merged dropdown listing every known field across all record shapes. This is *still
  strictly better* than today (no need to remember/pick the right of 9 block types up
  front), but it isn't "only shows what's valid" — it's "shows everything, same as
  picking blind today, just in one block instead of nine."

A stretch-goal extension that could close this gap is called out at the end, but the
core plan below stands on its own even without it.

## Proposed design (Phase 1 — core collapse)

1. **New single block type**, e.g. `recordField`, replacing the 9 `RECORD_FIELD_BLOCKS`
   as the thing newly authored in the toolbox (the 9 old types stay registered
   forever for back-compat — see "Back-compat" below, this is a hard-learned lesson
   from the legacy-accessor-block incident earlier this session).

2. **A lookup table keyed by check string**, built once from the existing spec data
   (no field data duplicated):
   ```typescript
   const FIELD_SET_BY_CHECK: Record<string, RecordFieldSpec[]> =
       Object.fromEntries(RECORD_FIELD_BLOCKS.map((spec) => [spec.targetCheck, spec.fields]));

   const ALL_FIELDS_MERGED: RecordFieldSpec[] = dedupeByName(
       RECORD_FIELD_BLOCKS.flatMap((spec) => spec.fields),
   );
   ```
   `dedupeByName` needs a one-time audit of whether any field name is reused across
   shapes with a genuinely different label/meaning (a quick scan while writing this
   plan found none — `Symbol`, `Type`, `Name`, `Waypoint` etc. all carry the same
   `{de, en}` label wherever they recur — but this should be explicitly re-verified
   as an implementation step, not assumed).

3. **Dynamic `FIELD` dropdown**, resolved per-instance from `TARGET`'s current
   connection, same `FieldDropdown(() => ...)` pattern `registerRecordFieldBlock`
   already uses, just reading the connected block instead of a fixed spec:
   ```typescript
   function fieldOptionsFor(block: Blockly.Block): [string, string][] {
       const targetBlock = block.getInputTargetBlock("TARGET");
       const checks = targetBlock?.outputConnection?.getCheck() ?? null;
       const matchedCheck = checks?.find((c) => FIELD_SET_BY_CHECK[c]);
       const fields = matchedCheck ? FIELD_SET_BY_CHECK[matchedCheck] : ALL_FIELDS_MERGED;
       return fields.map((f) => [t(f.label), f.name]);
   }
   ```
   `TARGET`'s own check should accept every known record check string (the union of
   all `targetCheck` values) plus `null` so a variable/temp-ref can still connect —
   Blockly already allows a `null`-checked block into any checked input, so this is
   just "don't over-narrow `TARGET` itself."

4. **Output check** (Milestone 13's live-recompute pattern, same as
   `registerRecordFieldBlock`'s `onchange`): recompute from whichever field is
   currently selected, falling back to `null` if the selected field name isn't in
   whatever field set is currently showing (can happen transiently if `TARGET`'s
   connection changes after a selection was made — same edge case dynamic dropdowns
   already have to handle elsewhere in this codebase, e.g. `sk_param_get`).

5. **Compiler**: add `"recordField"` to `GENERIC_ACCESSOR_TYPES` in `Compiler.fs` —
   the one-line addition the existing pattern establishes. No `Validator.fs` change.

6. **Toolbox** (`toolbox-de.ts:124-127`): swap `catalogRecordFieldBlockTypes` for a
   single new `["recordField"]` entry in the "Zugriffe" category's sort input (still
   concatenated with `dynamicAccessorTypes` for per-custom-block accessors, unchanged).
   `catalogRecordFieldBlockTypes` itself (still `RECORD_FIELD_BLOCKS.map(...)`) stays
   exported for `blocks-catalog.test.ts`'s existing assertions and stays used
   wherever back-compat rendering needs the full historical type list (see below).

## Back-compat (non-negotiable, per this session's legacy-accessor incident)

`RECORD_FIELD_BLOCKS` and its 9 (now 21) registered types **must not be removed or
deregistered** — exactly the bug fixed earlier this session (`636dce1`), where
removing 29 old block types without a compatibility shim broke every previously-saved
program/custom block using them (`Invalid block definition for type: ...`, workspace
rendered empty). `registerRecordFieldBlock` calls stay exactly as-is; only the
*toolbox* stops offering them for new authoring. A program saved last week using
`shipField` must keep opening and compiling identically after this change.

## Custom-block structured outputs — likely out of scope for v1

Custom blocks with a structured return value get their own per-custom-block accessor
types generated at runtime (`registerDynamicAccessorBlock`,
`accessor_<customBlockId>_<field>`), not part of `RECORD_FIELD_BLOCKS`. Folding these
into the same generic block would require the dropdown to also know about
whichever custom blocks exist in the *current* program — solvable (the toolbox
already threads `dynamicAccessorTypes` through), but adds real complexity for a
narrower payoff (custom-block outputs are already labeled per-block, one label
each). Recommend leaving these as their own dynamically-generated types for v1 and
revisiting only if it turns out to be a common pain point after the fixed-catalog
collapse ships.

## Verification

- `dotnet build`/`dotnet test` — the only F# change is the one-line
  `GENERIC_ACCESSOR_TYPES` addition; existing `Compiler.fs`/`Validator.fs` tests for
  the current 9 types should be extended (or duplicated) to also compile through
  `"recordField"`.
- `npm test` (`blocks-catalog.test.ts`) — extend to cover: (a) dropdown narrows
  correctly when a known info block is wired directly into `TARGET`, (b) dropdown
  falls back to the merged list when nothing/an unchecked block is wired in, (c) the
  old 9 types still register and still appear in whatever back-compat rendering path
  a previously-saved workspace exercises.
- Manual check via the `run` skill, isolated DB
  ([[feedback_isolate_db_for_live_verification]]): open a program, drag the new
  block, wire `getShipInfo` straight into it → confirm the dropdown shows only the 6
  ship fields; wire it through a `merke ... als` variable instead → confirm the
  dropdown shows the full merged list; open an old saved program that already uses
  e.g. `shipField` → confirm it still renders and simulates correctly, untouched.

## Stretch goal (separate, higher-risk follow-up): narrow through variables too

Blockly's variable model natively supports an optional `type` tag per variable
(`workspace.createVariable(name, type, id)`) — designed for exactly this kind of
typed-variable scenario, though unused anywhere in this codebase today. A possible
Phase 2: when a `merke ... als` (`SetVariable`) block's `VALUE` input is wired to a
block with a known record check, stamp that check onto the variable's `type` field;
`recordField`'s dropdown generator then also checks
`workspace.getVariableById(targetBlock.getField("VAR").getVariable().getId())?.type`
when `TARGET` is a bare `variables_get`, narrowing the variable-mediated case too.
This is meaningfully riskier (variables get renamed/reassigned/reused for different
values over a program's life; the tag would need to be re-derived on every edit, not
just at creation, and would need a clear rule for what happens when a variable is
reassigned to a different record shape mid-program) and isn't needed for the core
collapse to be a net improvement, so it's called out here as a follow-up idea, not
part of this plan's scope.
