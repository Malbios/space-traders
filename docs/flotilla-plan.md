# Flotilla: multi-ship programs (`mitSchiff` + `parallel`)

Status: **F1 and F2 shipped.** `mitSchiff` has the DSL/compiler/runtime path,
dynamic lock acquisition, waiting status, optional `falls nicht verfügbar` /
`if unavailable` branch, Blockly block/toolbox entry, and
scheduler/compiler/integration coverage. `parallel` has a mutator-driven Blockly
block, compiler/validator support, fork/join branch execution with
branch-targeted API/info/reconciliation effects, nested branch routing,
same-job ship-scope contention detection, and branch status lines on pilot
cards. Live browser verification passed via `scripts/verify-flotilla.mjs`
(Blockly round-trip for mutator state, Flottille toolbox, two-ship parallel
program completing end-to-end against `SpaceKids.FakeSpaceTraders`).

"Flotilla" is the informal name for this capability, not a separate saved
data concept — see the last decision below for why.

## Why

Today a "program" and a "job" are ship-agnostic-by-construction: no DSL block
carries a ship symbol, the compiler never threads one through, and the
scheduler binds exactly one ship to a job at pilot-pick time
(`JobState.shipSymbol: string option`, set once, injected implicitly at every
dispatch site in `Step.fs`). One program flies one ship, and one job has
exactly one instruction pointer (`JobState.position: PathEntry list`) — no
concurrency within a program at all, only across independently-started jobs.

Flotilla adds two DSL primitives to close this gap:

1. **`mitSchiff` — a scope block that changes which ship the blocks inside it
   act on.** `navigate`/`dock`/`extract`/etc. keep working exactly as today
   (act on "whichever ship is current") outside any `mitSchiff` block; nested
   inside one, "current" becomes that block's ship for everything in its
   body, sequentially.
2. **`parallel` — a generic "do these branches at the same time" block.**
   N instruction lists run concurrently within one job; the block as a whole
   completes once every branch has. Combined with `mitSchiff`, this is what
   actually enables "ship A mines while ship B hauls" from a single program
   — `mitSchiff` alone is still strictly sequential.

Shipped as **two separate milestones in sequence** — `mitSchiff` (F1) first,
`parallel` (F2) after, since F2 builds directly on F1's per-scope ship-lock
mechanics and is the harder of the two.

## Decisions made

- **Ship locks are acquired/released dynamically per `mitSchiff` scope**
  (entry acquires, exit releases) — not acquired all-at-once up front for the
  job's whole lifetime. More precise (a ship is only unavailable to other
  jobs while actually in use), at the cost of real runtime lock-acquisition
  logic mid-program (see F1 below for how contention/deadlock risk is
  handled).
- **`mitSchiff`'s ship argument is any expression** (a variable, an accessor
  result, a loop variable) — not restricted to a literal string. This is what
  makes "for each ship in my fleet, do X" possible at all.
- **A failing `parallel` branch does not abort its siblings.** Healthy
  branches run to their own natural completion/failure; the `parallel`
  block's own aggregate result (did *any* branch fail?) is only reported once
  every branch has settled. Matches this project's existing philosophy of
  never abandoning an in-flight action.
- **Nested `parallel` is allowed** (a `parallel` block's branch may itself
  contain another `parallel` block) — a real fork/join tree, not just one
  flat level of concurrency.
- **Ship-lock contention between two different jobs waits indefinitely, with
  no automatic timeout.** A `mitSchiff` scope that wants a ship another job
  currently holds just waits — visible in the dashboard as e.g. "Schiff
  FAKE-AGENT-2 wartet auf ein anderes Programm" — until that job releases it,
  or the kid manually pauses/cancels one of the two jobs themselves. No
  arbitrary duration to pick and get wrong; a real action can legitimately
  run for many real-world minutes, so a short bound risks spurious failure,
  and this matches the project's existing pattern of surfacing a stuck state
  rather than guessing a timeout (see the exception carved out for *within
  the same job*, below).
- **A small, narrowly-scoped catchable-failure construct is added** — but
  only for "this ship reference didn't resolve to a real, owned ship"
  (typo'd/nonexistent symbol), since indefinite waiting (above) removes the
  only other way a `mitSchiff` scope could fail. Something like `mitSchiff
  <shipExpr> sonst: [...]` (an if/else-shaped variant, not a general
  try/catch): the `sonst` branch runs if the ship reference doesn't resolve,
  otherwise the normal body runs. This is deliberately **not** a general
  exception-handling mechanism for arbitrary DSL failures (bad waypoint, bad
  trade good, etc. all still just end the job, exactly as today) — it is the
  first error-handling construct in the language, kept as small as possible.
- **Same-job self-deadlock is a distinct case from cross-job contention, and
  needs its own handling.** Two sibling branches of the same `parallel`
  block can each hold a ship the other wants — unlike cross-job contention,
  there's no *other* job for the kid to go pause/cancel to unstick it; the
  job is stuck against itself. Since a job already knows which ships its own
  branches currently hold (no cross-process coordination needed for this
  specific check), the scheduler should detect "a sibling branch of mine
  already holds this ship" immediately when a `mitSchiff` scope tries to
  acquire it, and treat that as a resolved-ship failure right away (routed
  through the same `sonst` mechanism above) rather than waiting indefinitely
  for something that can never happen on its own.
- **No separate "named flotilla" concept.** `JobRunner.fs`'s existing
  `getFleetInfo` accessor already returns the whole fleet as a `VList` of
  ship records (`client.ListShips`, `Compiler.fs`'s `ACCESSOR_BLOCKS`).
  Combined with a stock `for`-loop and `mitSchiff`'s new dynamic-expression
  support, "do X to every ship I own" is already fully expressible with zero
  new persistence or UI:
  ```
  for each ship in getFleetInfo():
      mitSchiff ship.Symbol:
          navigate ...
  ```
  A saved/named ship-group would only be a convenience layer on top (skip
  re-filtering the fleet each time) — it adds no capability `mitSchiff` +
  `parallel` + `getFleetInfo` don't already have together. Not part of this
  plan; can be revisited later as a small, independent, purely-additive
  feature if it turns out to be missed in practice.

## Milestone F1 — `mitSchiff`

**DSL/compiler.** A new C-shaped block, `mitSchiff <shipExpr>: [...]`
(`blocks-catalog.ts`), lexically scoped like a `for`-loop body, accepting any
expression that evaluates to a ship symbol string, plus its `sonst`
counterpart (a second body slot, mutator-added, entered instead of the main
body if the ship reference doesn't resolve — see "Decisions made"). Because
the ship is a runtime value (not knowable at compile time), the compiler can
no longer bake a ship symbol directly into a compiled action the way it can
bake a literal waypoint. Instead: `JobState` gains a runtime "current ship"
stack; entering a `mitSchiff` block evaluates its expression and pushes the
result, exiting pops it. Every ship-scoped action's dispatch site in
`Step.fs` reads the top of that stack (falling back to the job's
default/primary ship when empty) instead of a single static `job.shipSymbol`.

**Ship locks — the part that got harder.** Since the ship isn't known until
execution actually reaches a `mitSchiff` block (and can differ per loop
iteration), lock acquisition becomes a genuine runtime operation, not a
one-time static computation. Two distinct cases, handled differently (see
"Decisions made"):
- **Ship reference doesn't resolve at all** (typo, not owned, or — new —
  already held by a sibling branch of the *same* job): fails immediately,
  routed through the `sonst` branch if present, otherwise ends the job with
  a clear message. This is a cheap, purely-local check (does this symbol
  exist in the agent's fleet? does one of my own other branches already hold
  it?) — no waiting involved.
- **Ship is legitimately held by a *different* job**: a new `JobStatus` case
  for "waiting for ship X, held by another program" (same shape as
  `WaitingForCooldown`/`AwaitingApiResponse` — the tick loop just keeps
  checking until the lock frees up), with **no automatic timeout**. Resolved
  externally: the other job finishes/releases the ship, or the kid pauses/
  cancels one of the two jobs from the dashboard.

**UI.** Since the full set of ships a program might touch isn't statically
knowable anymore (dynamic expressions), there's no "pick every ship up
front" flow to build. Start-time picking stays exactly as it is today — one
default/primary ship for the job. Any other ship gets locked the moment
execution enters a `mitSchiff` scope for it, and the dashboard should surface
whichever ship is currently active (reusing the same "what's active right
now" mechanism `Step.blockIdPerFrame` already gives the custom-block call
stack).

**UI (locks).** The dashboard needs to render the new "waiting for ship X,
held by another program" status clearly (German copy, e.g. "wartet auf ein
anderes Programm") — this is a real waiting state a kid needs to notice and
be able to act on (go pause/cancel the other job), not a transient blip.

**Verification.** `SchedulerTests.fs`: a program with two `mitSchiff` blocks
using *dynamic* ship expressions (e.g., a loop variable from iterating
`getFleetInfo()`), asserting actions dispatch against the right ship each
iteration. A test proving a job waiting on a lock held by a *different* job
stays waiting indefinitely (no spurious failure) and proceeds the moment
that job releases it. A test proving an unresolvable ship reference (typo)
routes into `sonst` when present, or ends the job clearly when absent.
`JobRunnerTests.fs`: end-to-end against the fake's two seeded ships.
Playwright: a program iterating the fake's fleet with `mitSchiff`, watched to
completion; a live two-job contention scenario showing the waiting status
and resolving once the other job is cancelled.

## Milestone F2 — `parallel`

Builds directly on F1 — branches are themselves ordinary instruction lists
that may contain `mitSchiff` blocks, nested `parallel` blocks, or neither.

**DSL/compiler.** A new block with N branch slots (mutator-driven "add a
branch" gear icon, same UI pattern as custom blocks' typed-input mutator).
Compiles to a new `Instruction` case carrying N independent instruction
lists, each of which may itself contain further `parallel`/`mitSchiff`
blocks.

**Scheduler — the real architectural change.** `JobState` needs a genuine
fork/join tree, not a flat list, since nested `parallel` is allowed: a
`Branch` is either a leaf (its own `position`/`stack`/status/current-ship)
or itself a `parallel` node holding child branches. This ripples into:
- **Recursive status aggregation.** A `parallel` node's own status is
  derived from its children (recursively, for nested ones) — "running"
  while any child is, "settled" (successfully or not) once every child,
  including deeply nested ones, has settled.
- **Per-branch reconciliation.** Up to as many actions as there are live
  leaf branches can be genuinely in flight at once, each independently
  hitting the existing `AwaitingApiResponse`/`Reconciling` ambiguous-failure
  machinery — no longer able to assume job-wide exclusivity.
- **Per-branch pause/cancel deferral.** Each leaf branch defers a
  pause/cancel request independently until its own in-flight action
  resolves; a `parallel` node (and the job overall) only reaches
  `Paused`/`Cancelled` once every leaf, recursively, has settled.
- **Failure aggregation, not abortion.** Per the decision above: a failing
  leaf doesn't cancel its siblings. A `parallel` node's own result becomes
  "failed" if any child (recursively) failed, computed once every child has
  settled — never before.

**Ship locks — same-job self-deadlock is the new risk here.** Each leaf
branch acquires/releases its own ships dynamically via F1's per-`mitSchiff`-
scope mechanism. Cross-job contention is unchanged from F1 (indefinite wait,
resolved externally). But `parallel` introduces a case F1 alone couldn't:
**two sibling branches of the same job** each holding a ship the other
wants — genuine self-deadlock, and unlike cross-job contention, there's no
external job for the kid to go pause/cancel. Per "Decisions made," this must
be detected immediately (a branch checks whether a *sibling* already holds
the ship it wants, which is purely local state — no cross-process
coordination needed) and routed through `mitSchiff`'s `sonst` branch, never
left to wait indefinitely for a release that can't happen.

**UI.** A job running a `parallel` block shows every leaf branch as its own
line under the job's card, indented by nesting depth, with its current ship
+ status — reusing the same plain list-rendering style already used for
ships/waypoints/contracts elsewhere in this app, e.g.:
```
Pilot Finn (Programm "Bergbau-Team")
  └ FAKE-AGENT-1: navigiert nach X1-TEST-B2
  └ FAKE-AGENT-2: wartet auf Abkühlung
     └ (verschachtelt) FAKE-AGENT-3: andockt
```

**Verification.** `SchedulerTests.fs`: two branches genuinely interleaving
(prove via a fake clock that branch B's second action can dispatch before
branch A's first one resolves). A nested-`parallel` test (a branch containing
its own `parallel` block, at least 3 levels of leaf visible). **Two sibling
branches of the same job each holding a ship the other wants** — must
resolve immediately via `sonst`/job failure, not hang (this is the specific
self-deadlock case above, and the one most worth a dedicated test — it's the
one scenario indefinite waiting can never resolve on its own). Two branches
of *different* jobs racing for the same ship — should behave exactly like
F1's cross-job case (one waits, indefinitely, until the other releases). An
integration test proving branches within the same job don't cross-
contaminate reconciliation state (the sibling to Milestone 10 Part C's
existing cross-job version, but harder: within one job). Live Playwright: one
program, one job, two ships genuinely doing different things at the same
time, confirmed via the dashboard's indented per-branch view.

## Status

F1 and F2 are shipped with automated scheduler/compiler/integration coverage
and a live browser check (`scripts/verify-flotilla.mjs` — run with
`SpaceKids.FakeSpaceTraders` on :5196 and `SpaceKids.Server` on :5290, with
`SpaceTraders__BaseUrl=http://localhost:5196/` and
`ASPNETCORE_ENVIRONMENT=Development`).
