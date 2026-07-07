# Flotilla: multi-ship programs (`mitSchiff` + `parallel`)

Status: **planning only, not started.** All major design decisions are now
settled (see "Decisions made" below) — this doc is a concrete milestone
breakdown ready to hand to Part A.

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
expression that evaluates to a ship symbol string. Because the ship is a
runtime value (not knowable at compile time), the compiler can no longer bake
a ship symbol directly into a compiled action the way it can bake a literal
waypoint. Instead: `JobState` gains a runtime "current ship" stack; entering
a `mitSchiff` block evaluates its expression and pushes the result, exiting
pops it. Every ship-scoped action's dispatch site in `Step.fs` reads the top
of that stack (falling back to the job's default/primary ship when empty)
instead of a single static `job.shipSymbol`.

**Ship locks — the part that got harder.** Since the ship isn't known until
execution actually reaches a `mitSchiff` block (and can differ per loop
iteration), lock acquisition becomes a genuine runtime operation that can
contend with other jobs, not a one-time static computation. This needs:
- A new `JobStatus` case for "blocked trying to acquire a ship lock right
  now" (same shape as `WaitingForCooldown`/`AwaitingApiResponse` — the tick
  loop just keeps retrying until it succeeds or gives up).
- A **bounded** retry/backoff, not indefinite blocking and not strict
  deadlock-free lock ordering (ordering isn't available anymore — the ship
  isn't known ahead of time, so there's nothing to sort upfront). Mirrors
  this project's existing `busy_timeout`/`RequestQueue` retry-backoff
  conventions.
- A clear failure once the bound is exceeded — "couldn't get ship X, another
  job is using it" — rather than hanging forever. This is a deliberate
  simplification over real deadlock *detection*: two jobs each holding one
  ship the other wants will both eventually time out and fail clearly,
  instead of the system trying to detect and break the cycle. Consistent
  with this project's "never blindly retry forever" principle.

**UI.** Since the full set of ships a program might touch isn't statically
knowable anymore (dynamic expressions), there's no "pick every ship up
front" flow to build. Start-time picking stays exactly as it is today — one
default/primary ship for the job. Any other ship gets locked the moment
execution enters a `mitSchiff` scope for it, and the dashboard should surface
whichever ship is currently active (reusing the same "what's active right
now" mechanism `Step.blockIdPerFrame` already gives the custom-block call
stack).

**Verification.** `SchedulerTests.fs`: a program with two `mitSchiff` blocks
using *dynamic* ship expressions (e.g., a loop variable from iterating
`getFleetInfo()`), asserting actions dispatch against the right ship each
iteration. A test proving a `mitSchiff` scope waiting on a lock another job
holds eventually times out with a clear failure rather than hanging.
`JobRunnerTests.fs`: end-to-end against the fake's two seeded ships.
Playwright: a program iterating the fake's fleet with `mitSchiff`, watched to
completion.

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

**Ship locks.** Each leaf branch acquires/releases its own ships dynamically
via F1's per-`mitSchiff`-scope mechanism, independent of its siblings.
Concurrent branches raise the contention surface (more simultaneous lock
attempts, including two branches racing for the *same* ship, or two branches
each holding what the other currently wants) — F1's bounded-retry-then-fail
approach should already cover this correctly, but it's exactly the scenario
F2's verification needs to specifically stress, not just assume carries over.

**UI.** A job running a `parallel` block needs its dashboard card to show
every leaf branch's current ship and status at once (recursively reflecting
nesting) — a real UI design question (indented tree? one row per leaf?), not
a copy-paste of today's single-line pilot card.

**Verification.** `SchedulerTests.fs`: two branches genuinely interleaving
(prove via a fake clock that branch B's second action can dispatch before
branch A's first one resolves). A nested-`parallel` test (a branch containing
its own `parallel` block, at least 3 levels of leaf visible). Two branches
racing for the same ship, and two branches each holding what the other
wants, both asserting a clean bounded-timeout failure rather than a hang or
crash. An integration test proving branches within the same job don't
cross-contaminate reconciliation state (the sibling to Milestone 10 Part C's
existing cross-job version, but harder: within one job). Live Playwright: one
program, one job, two ships genuinely doing different things at the same
time, confirmed via the dashboard's per-branch view.

## Smaller details to settle during Part A

Not blocking, but worth deciding deliberately rather than by accident once
implementation starts:
- The bounded-retry timeout duration for ship-lock acquisition (F1) — long
  enough to ride out normal contention, short enough that a genuinely stuck
  program fails within a reasonable time for a kid to notice.
- How a `mitSchiff` scope's lock-acquisition failure surfaces to the DSL —
  does the whole job fail, or is it catchable/retryable from within the
  program itself (the latter is more powerful but a bigger DSL addition)?
- The dashboard's exact visual shape for a multi-branch job (F2) — sketch it
  before writing Bolero code, same as the entity-inspector/system-map
  feature did.
