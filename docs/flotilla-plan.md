# Flotilla: multi-ship programs

Status: **planning only, not started.** Design direction is now settled (see
below); this doc is a concrete milestone breakdown, not a locked spec down to
the last field name.

## Why

Today a "program" and a "job" are ship-agnostic-by-construction: no DSL block
carries a ship symbol, the compiler never threads one through, and the
scheduler binds exactly one ship to a job at pilot-pick time
(`JobState.shipSymbol: string option`, set once, injected implicitly at every
dispatch site in `Step.fs`). One program flies one ship, and one job has
exactly one instruction pointer (`JobState.position: PathEntry list`) — no
concurrency within a program at all, only across independently-started jobs.

Flotilla adds two things to close this gap:

1. **`mitSchiff` — a scope block that changes which ship the blocks inside it
   act on.** `navigate`/`dock`/`extract`/etc. keep working exactly as today
   (act on "whichever ship is current") outside any `mitSchiff` block; nested
   inside one, "current" becomes that block's ship for everything in its
   body, sequentially.
2. **`parallel` — a generic "do these branches at the same time" block.**
   N instruction lists run concurrently within one job; the block as a whole
   completes once every branch has. Combined with `mitSchiff`, this is what
   actually enables "ship A mines while ship B hauls" from a single program
   — `mitSchiff` alone is still strictly sequential (one ship's block fully
   finishes before the next `mitSchiff` block even starts).

These two are deliberately shipped as **two separate milestones in sequence**
below, not one combined change — `mitSchiff` alone needs no new concurrency in
the scheduler at all (still exactly one thing in flight at a time, just
targeting a different ship over time), while `parallel` is a real
architectural change (multiple concurrent instruction pointers, per-branch
reconciliation, per-branch pause/cancel). Shipping `mitSchiff` first gets
real value out the door and exercises the multi-ship-lock groundwork that
`parallel` needs anyway, before taking on fork/join semantics.

## Milestone F1 — `mitSchiff` (sequential multi-ship addressing)

**DSL/compiler.** A new C-shaped block, `mitSchiff "SHIP-2": [...]`
(`blocks-catalog.ts`), lexically scoped like a `for`-loop body. `Compiler.fs`
tracks a "current ship" context that `mitSchiff` pushes on entry / pops on
exit; every ship-scoped action/info block compiles with whichever ship symbol
is current at that point in the program (a literal for now — see the
"restriction" note below). Outside any `mitSchiff`, current ship is the job's
default/primary ship, exactly like today. `Validator.fs` gains a check: every
`mitSchiff` block's ship-symbol argument must be a literal (not a variable or
expression) for Milestone F1 — dynamic ship references are out of scope until
static analysis of "every ship this program could possibly touch" is no
longer required for locking (see below); a clear, locale-aware error covers
the rejected case.

**Scheduler.** Each compiled action needs to carry (or the scheduler must be
able to derive) which ship it targets, instead of the dispatch sites in
`Step.fs` reading a single static `job.shipSymbol` — likely a `shipSymbol:
string` field added onto `QueuedAction`'s action cases, populated by the
compiler from whatever was current at compile time. `JobState` still has
exactly one instruction pointer and at most one in-flight action — this is
the part that keeps F1 tractable: reconciliation, pause/cancel, and the
`AwaitingApiResponse`/`Reconciling` machinery all still work exactly as today,
just keyed by "the ship this specific action carries" instead of "the job's
one ship."

**Ship locks.** Since every ship a program can touch is now known statically
(literal ship symbols only, per the Validator restriction above), acquire
*all* of them atomically when the job starts — sorted into a canonical order
first (e.g. by ship symbol) to avoid a circular-wait deadlock against another
job trying to acquire an overlapping set — and hold all of them for the job's
full lifetime, released together on any terminal state. This sidesteps a
much harder problem (acquiring/releasing locks dynamically as execution
enters/exits each `mitSchiff` scope) entirely, at the cost of a job holding a
ship's lock even during long stretches of the program where it isn't
currently "in" that ship's `mitSchiff` block. Worth being upfront with the
user about that tradeoff before building it — the alternative (acquire on
`mitSchiff` entry, release on exit) is real but meaningfully more complex and
not obviously worth it for a first version.

**UI.** Starting a program that contains any `mitSchiff` block needs a
"pick every ship this program uses" flow instead of today's single-ship
picker (the set is knowable statically, same list the lock-acquisition logic
above needs) — likely surfaced as "this program needs N ships, assign them:"
rather than a generic multi-select, so a kid can see which ship plays which
role. The pilot dashboard's card for such a job should show all its ships,
plus which one is "current" right now (reusing the same
`Step.blockIdPerFrame`-style "what's active right now" mechanism the
custom-block call stack already has, generalized from "which stack frame" to
"which ship").

**Verification.** `SchedulerTests.fs`: a program with two `mitSchiff` blocks
back to back, asserting actions dispatch against the right ship in the right
order. `JobRunnerTests.fs`: end-to-end against the fake's two seeded ships.
Playwright: a program using both `FAKE-AGENT-1` and `FAKE-AGENT-2` in
sequence, started, watched to completion.

## Milestone F2 — `parallel` (real concurrency within one job)

Builds on F1 — a `parallel` block's branches are themselves ordinary
instruction lists that may contain `mitSchiff` blocks (or act on the job's
default ship if they don't).

**DSL/compiler.** A new C-shaped-with-multiple-bodies block, `parallel:
[branch 1] [branch 2] ... [branch N]` (however many branch slots Blockly's
mutator UI lets a kid add — same "gear icon to add an input" mutator pattern
already used for custom blocks' typed inputs). Compiles to a new
`Instruction` case carrying N independent instruction lists.

**Scheduler — the real architectural change.** `JobState` needs multiple
concurrent instruction pointers, not one. Concretely: replace (or augment)
the single `position`/`stack` with something like a `Branch` record —
`{ id; position: PathEntry list; stack: Frame list; status: BranchStatus;
currentShip: string option }` — and a job executing a `parallel` block holds
a `Branch list`, each stepped independently. This ripples into:
- **Per-branch status**, not one job-wide status: one branch can be
  `AwaitingApiResponse` while a sibling is `WaitingForCooldown` or already
  `Completed`. The job's own overall status becomes derived (e.g. "Running"
  while any branch is; "Completed" only once every branch is; **what
  happens if one branch fails while siblings are still running is a real
  product decision to make explicitly** — does the whole job fail
  immediately (siblings aborted), or do healthy branches keep going until
  the `parallel` block's join, surfacing the failure only then? Flag this to
  the user before building it, don't default silently).
- **Per-branch reconciliation.** Today's ambiguous-failure handling assumes
  a job has at most one in-flight action ever. With N branches, up to N
  actions can be genuinely in flight at once, each needing its own
  independent `Reconciling` hop — this is the existing per-action mechanism,
  just no longer able to assume job-wide exclusivity.
- **Per-branch pause/cancel deferral.** `pausePending`/`cancelPending`
  currently defer job-wide until the one in-flight action resolves. With N
  branches, each branch defers independently until *its own* in-flight
  action resolves; the job only reaches `Paused`/`Cancelled` once every
  branch has settled.
- **Nesting.** Can a `parallel` block appear inside another `parallel`
  block's branch? Structurally recursive and probably fine to allow, but
  adds real complexity to lock ordering and status aggregation — consider
  explicitly disallowing nested `parallel` for a first version (Validator
  check) and revisiting once flat `parallel` is proven out.

**Ship locks.** Extends F1's "acquire every statically-known ship up front"
approach — a `parallel` block's branches only add more ships to that same
static set, no new acquisition-timing complexity, *as long as* nested
`parallel` is disallowed (see above) and every `mitSchiff` ship symbol stays
a literal.

**UI.** A job running a `parallel` block needs its dashboard card to show
every branch's current ship and status at once, not one line — a real UI
design question (a mini-table per job? one row per branch, indented under
the job?), not just a copy-paste of the existing single-line pilot card.

**Verification.** `SchedulerTests.fs`: two branches genuinely interleaving
(prove via a fake clock that branch B's second action can dispatch before
branch A's first one resolves, not just "both eventually finish"). An
integration test proving two branches *within the same job* don't
cross-contaminate reconciliation state — the sibling to Milestone 10 Part
C's existing "two concurrent jobs don't cross-contaminate" test, but the
harder within-one-job version. Live Playwright: one program, one job, two
ships genuinely doing different things at the same time (e.g. one mining,
one hauling toward a delivery), confirmed via the dashboard's per-branch
view.

## Open questions to raise with the user before starting

1. **F1's "acquire all ships up front, hold for the job's whole lifetime"
   lock strategy** — confirmed acceptable, or is per-`mitSchiff`-scope
   acquire/release worth the extra complexity from the start?
2. **F1's literal-only restriction on `mitSchiff`'s ship argument** — fine
   for a first version, or does the feature feel too limited without
   dynamic ship references (e.g. "whichever ship is idle") from day one?
3. **F2's branch-failure semantics** — does one branch failing abort its
   siblings immediately, or do they run to their own natural
   completion/failure independently, with the `parallel` block only
   surfacing the aggregate result at the join?
4. **F2's nested-`parallel` restriction** — confirmed out of scope for a
   first version?
5. Does a flotilla need to be a *named, reusable* group of ships (saved
   somewhere, like custom blocks or programs are), or is it always just "the
   ships this particular job happens to touch," picked fresh each run?
