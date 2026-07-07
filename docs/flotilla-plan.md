# Flotilla: multi-ship programs

Status: **planning only, not started.** Written as a brainstorm + concrete
milestone breakdown, not a locked design — the open question in "Design
options" below is genuinely open; pick one (or something better) before
starting Part A.

## Why

Today a "program" and a "job" are ship-agnostic-by-construction: no DSL block
carries a ship symbol, the compiler never threads one through, and the
scheduler binds exactly one ship to a job at pilot-pick time
(`JobState.shipSymbol: string option`, set once, injected implicitly at every
dispatch site in `Step.fs`). One program flies one ship. If a kid wants two
ships doing coordinated things (one mines, one hauls; one explores while
another trades), they have to save the *same* program twice and start it on
two different pilots independently — there is no way for one program to
address more than one ship, and no way for two running instances of a program
to coordinate at all.

Flotilla means a single program can reference, and act through, more than one
ship.

## Design options for "which ship does this block act on"

This is the crux of the whole feature and should be settled deliberately, not
by momentum. Three shapes, roughly in order of how much they disrupt the
existing block catalog:

**Option 1 — implicit "current ship" + an explicit `mitSchiff X: ...` scope
block.** Every existing block (`navigate`, `dock`, `extract`, ...) keeps
working exactly as today, unchanged, acting on "whichever ship is current."
A new C-shaped block, e.g. `mitSchiff "SHIP-2": [...]`, changes what "current"
means for every block nested inside it (stack-scoped, like a `for`-loop
body). Plain: `navigate X1-A1` outside any `mitSchiff` block still means "the
job's default/primary ship," same as today.
- *Pro*: zero changes to the ~20 existing action/info blocks, zero migration
  risk for saved programs, reads naturally ("with ship X, do: ..."), and it's
  a very small DSL surface (one new block, one new compiler concept: a
  lexical "current ship" that can be pushed/popped).
- *Con*: doesn't compose well if a kid wants "ship A does X *while* ship B
  does Y" (concurrent, not sequential) — `mitSchiff` blocks would still run
  strictly in program order, one ship fully done before the "with"-block for
  the next ship even starts its first action. Good for round-robin
  choreography, not true parallelism.

**Option 2 — every ship-scoped block gains an optional ship-reference input.**
`navigate(waypoint)` becomes `navigate(waypoint, ship?)` where the socket
defaults to "current ship" if left empty (matching your own instinct in the
prompt: "navigate" for current ship, "navigate x" for a specific one). Ship
references come from a new reporter block, e.g. `meinSchiff` (the job's
default ship) or `schiffAusFlotte "SHIP-2"` (a named member of the flotilla).
- *Pro*: most flexible expression-wise, fits naturally with existing
  `for`/`if`/variable blocks (a ship reference is just a value you can store
  in a variable and pass around), doesn't need a new C-shaped scope block.
- *Con*: touches all ~20 action blocks' Blockly definitions
  (`blocks-catalog.ts`) and the compiler's per-block-type argument tables
  (`Compiler.fs`), a materially bigger diff; every saved program still
  compiles (missing optional socket = default), so backward compatibility
  should hold, but it's more surface to get wrong.

**Option 3 — a genuinely separate concurrency primitive: "launch a copy of
this program on ship X" (fork), not a way to reference other ships inline.**
Closer to how real fleets are often scripted: one "coordinator" program
launches N "worker" jobs (each a normal, single-ship program, possibly the
*same* saved program), optionally passing simple parameters, and can wait
on/query their status. No changes to the existing single-ship block catalog
at all — the ship-scoped blocks never need to know about other ships.
- *Pro*: true parallelism for free (each forked job is scheduled
  independently, same as today's pilot dashboard already does); smallest
  change to `Step.fs`'s actual instruction-execution semantics (nothing about
  *how* an action runs changes, only *how many jobs exist*).
- *Con*: different mental model from "my program controls my fleet directly"
  — more like a light job-spawning API than in-line multi-ship scripting; the
  two forked jobs can't share program-local variables directly (only through
  something like contract/market state, or new explicit inter-job signaling,
  which is its own can of worms).

**A fourth option worth naming even though it's likely too much for one
milestone**: combine 1 or 2 with a real "parallel block" (`parallel: [branch
A] [branch B]`), giving the scheduler multiple concurrent instruction
pointers *within one job*. This is the most powerful and the most invasive —
`JobState`/`Step.fs`'s path/frame model would need real fork/join semantics,
not just a scoped ship reference. Worth a two-line callout in the milestone
kickoff conversation, but probably not Milestone-1 scope.

No recommendation is baked into the milestone breakdown below beyond
sequencing — Part A is written to be option-agnostic where possible, but
Part B (the actual compiler/block work) forks based on which option is
picked.

## What has to change regardless of which option wins

- **`JobState.shipSymbol: string option`** (`SpaceKids.Core/Scheduler/Types.fs`)
  is a single ship per job. Flotilla needs either (a) a job that can hold
  *multiple* locked ships at once (Options 1/2), or (b) no change here at all
  if Option 3 (fork) is chosen, since each forked job is still single-ship.
  This is the single highest-leverage design decision to nail down first,
  since it ripples into ship-lock acquisition, reconciliation, and every
  `Step.fs` dispatch site that currently reads `job.shipSymbol` directly.
- **Ship locks** (`ShipLockRepository.fs`) currently key one lock row per
  `(ship_symbol, job_id)` pair with a 1:1 assumption baked into the
  acquire/reclaim logic (see the `BEGIN IMMEDIATE` TOCTOU fix from the
  code-review session — that fix assumed one lock acquisition per call).
  Options 1/2 need N locks acquired atomically for one job (all-or-nothing,
  to avoid a job holding ship A but failing to get ship B and deadlocking
  another job that got B-then-wants-A) — this is a real distributed-lock
  ordering problem (classic: acquire in a canonical order, e.g. sorted by
  ship symbol, to avoid circular waits).
- **Reconciliation** (§13's ambiguous-failure handling) is already per-action,
  keyed by whichever ship the in-flight action targeted — this mostly
  survives unchanged for Options 1/2 as long as each `QueuedAction`/`Effect`
  still carries (or the scheduler can still derive) exactly one ship symbol
  per in-flight call. Don't let "flotilla" leak into "one API call touches
  two ships at once" — the real SpaceTraders API is one-ship-per-call
  regardless, which is a natural constraint to lean on.
- **UI**: the pilot dashboard's "one job = one ship" framing
  (`Main.fs`'s pilot cards, `pilotName`, watch mode) needs a multi-ship job to
  show *which* ships are involved, and (for Option 1/2) probably an
  indicator of "currently acting through ship X" the same way `Step.
  blockIdPerFrame` already drives the custom-block call-stack's "innen aktiv"
  indicator — likely the same mechanism, generalized.
- **Compiler/Validator**: whichever option is picked, a program referencing a
  ship that was never actually assigned to the job (e.g. a typo'd ship
  symbol, or a flotilla member removed after the program was written) needs a
  clear, locale-aware failure — this project's established pattern (German
  `failwith` messages, `Validator.fs`'s static checks catching what can be
  caught before the job even starts) should extend here, not be
  reinvented.

## Milestone breakdown

**Part A — pick the design, no code.** Resolve the Option 1/2/3 question above
(plus whether the 4th, real-parallel-block option is explicitly deferred or
folded in) with the user. Write the concrete DSL/compiler/scheduler shape
before touching any files — this is the one part of this whole feature
that's genuinely a product decision, not an engineering one.

**Part B — scheduler + persistence groundwork.** Whatever Part A decided,
implement the `JobState`/ship-lock changes needed to let one job legitimately
touch more than one ship (Options 1/2) or spawn sibling jobs (Option 3) —
before any new blocks exist. Test this at the `SchedulerTests.fs`/
`JobRunnerTests.fs` level first, same as every prior milestone (pure core,
then integration against the fake), independent of UI.

**Part C — DSL/compiler/block-catalog surface.** The actual new block(s)
(`mitSchiff`, or the optional ship-reference socket + `meinSchiff`/
`schiffAusFlotte` reporters, or a "start program on ship" action block for
Option 3), `Compiler.fs` support, `Validator.fs` checks for
referencing-a-ship-the-job-doesn't-have. Existing saved programs must still
compile and run identically (no ship reference = today's behavior) —
regression-test this explicitly, it's the thing most likely to silently
break.

**Part D — UI: choosing a flotilla, not just a pilot.** Today "Start" picks
one ship for one job. Flotilla needs a "pick 1-or-more ships" flow (or, for
Option 3, no change here at all — forking is triggered by a block, not a
Start-time picker), plus dashboard visibility into which ships a running job
is currently touching.

**Part E — verification.** A live end-to-end scenario with the fake server's
two seeded ships (`FAKE-AGENT-1`/`FAKE-AGENT-2`) doing something a single-ship
program provably couldn't: e.g. one mines while the other hauls to a
delivery contract, verified via Playwright same as every prior UI milestone,
plus an integration test proving two ships' actions from the *same* job don't
cross-contaminate reconciliation state (mirroring Milestone 10 Part C's
existing "two concurrent pilots don't cross-contaminate" test, but within one
job instead of across two).

## Open questions to raise with the user before Part A starts

1. Which of Options 1/2/3/4 (or a hybrid) actually matches what "manage
   multiple ships" should feel like for a kid — sequential choreography
   (Option 1), flexible references (Option 2), or spawn-and-forget parallel
   workers (Option 3)?
2. Does a flotilla need to be a *named, reusable* group of ships (saved
   somewhere, like custom blocks or programs are), or is it always just "the
   ships this particular job happens to touch," picked fresh each run?
3. Should a flotilla job still occupy one "pilot card" in the dashboard, or
   become its own dashboard concept (a "squadron" card showing N ships)?
