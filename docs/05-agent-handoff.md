# Current state

## Working

- Full solution scaffold: `SpaceKids.Client` (Bolero WASM), `SpaceKids.Server` (ASP.NET
  Core host), `SpaceKids.Core`, `SpaceKids.SpaceTraders`, `SpaceKids.FakeSpaceTraders`,
  plus `Core.Tests`/`Server.Tests`/`IntegrationTests`. Builds clean with `dotnet build
  SpaceKids.slnx`, no warnings.
- Real persistence foundation (§12, Milestone 1): `SpaceKids.Server/Persistence/` has a
  hand-rolled SQL migration runner (`MigrationRunner.fs`, tracked in `schema_versions`),
  the full 12-table core schema (`Migrations/0001_initial.sql`), WAL mode + busy_timeout
  (`Database.fs`), and an hourly `VACUUM INTO` backup task with 7-file retention
  (`Backup.fs`, runs on start/hourly/clean-shutdown). Verified end-to-end: `dotnet run`
  produces `spacekids.db` and an immediate `backups/backup_<timestamp>.db`; the
  Milestone 0 spike page's Speichern/Laden still round-trips, now via the real
  `workspaces` table (`WorkspaceRepository.fs`) instead of the retired
  `spike_workspaces` table. Details and the design rationale in `docs/decisions.md`.
- Blockly TS seam (`SpaceKids.Client/Blockly/blockly-host.ts`) bundled automatically as
  part of `dotnet build` (no manual npm step). Exposes `window.spaceKids.*` for F# to
  call via `IJSRuntime`.
- Milestone 0 spike page (`SpaceKids.Client/Main.fs`) proves, verified end-to-end in a
  real browser (Playwright): create a block by dragging from a German toolbox, save to
  real SQLite, reload the page, load from SQLite (same block reappears), highlight it,
  toggle read-only. Zero console errors.
- Custom-block mutator mini-spike: a definition-shell block with a real mutator (gear
  icon, add/remove a typed "Zahl" input), caller-block generation from a signature, and
  cross-workspace toolbox injection — verified for the 0-input path fully automated;
  the typed-input-add path verified visually (screenshot) rather than fully scripted.
  Details and exact reproduction steps in `docs/decisions.md`.
- Blockly pinned to exact version 13.1.0; confirmed procedure blocks live in core at
  this version (irrelevant either way — this project never uses them, see
  `docs/decisions.md`).
- Real SpaceTraders data (§19, Milestone 2): `SpaceKids.SpaceTraders` is a minimal API
  client (`getAgent`/`listShips`/`listContracts`/`listWaypoints`/`getMarket`) verified
  field-by-field against the real OpenAPI spec. Every call is routed through
  `SpaceKids.Server/RequestQueue.fs` (a single-lane stub logging to
  `request_queue_events`, per §13's "no ad hoc HTTP path" principle). Token flow is
  paste-an-existing-token (confirmed with the user, not self-registration — see
  `docs/decisions.md` for why). `SpaceKids.FakeSpaceTraders` now serves the same 5
  endpoints with a seeded fixture, and `SpaceKids.IntegrationTests` exercises the real
  client code against it via `WebApplicationFactory`. **Verified against the live API**
  with a real user-provided token: agent/ships/waypoints/market rendered correctly on
  the Milestone 0 spike page's new dashboard section, all 5 calls logged, and the
  persisted token survives a server restart (`loadDashboard`).
- Full German block catalog (§6/§7, Milestone 3): all 20 SpaceTraders-specific
  action/information blocks defined in `SpaceKids.Client/Blockly/blocks-catalog.ts`,
  documented in `docs/04-block-catalog.md`. The remaining 14 "programming" blocks from
  §6 turned out to already be stock Blockly blocks (already registered, already German
  via the existing locale) — no new code needed for those, just toolbox references. The
  main "Programm" workspace's toolbox (`buildCatalogToolbox` in `toolbox-de.ts`) now has
  6 categories: Aktionen, Informationen, Programmierung, Variablen, Eigener Block, Eigene
  Blöcke. A "Simuliere Ausführung" button walks and highlights the first block stack in
  sequence (`simulateRun` in `blockly-host.ts`) — a fake/simulated run, not real DSL
  execution (that's Milestone 4). Verified in a real browser (Playwright): all
  categories render, a catalog block drags/connects/saves/reloads correctly, simulate-run
  completes, zero console errors, and the Milestone 0 Part C mutator spike still works
  untouched.
- DSL compiler and static validator (§10/§11, Milestone 4): `SpaceKids.Core/Dsl/`
  compiles Blockly workspace JSON into a small internal DSL and validates it — pure
  library, no UI/Server wiring (that's a later milestone). `Compiler.compileWorkspace`
  recognizes all 20 catalog blocks plus the stock control/math/logic/list/variable
  blocks and `sk_show_message`/`sk_wait`, linearizing expressions per §10 (effectful
  info-block reads hoisted into `InfoRead` instructions writing `$tN` temps).
  `Validator.validate` covers §11's static checks (start block, scope, custom-block call
  arity/types, transitive closure, cycle detection); `revalidateAgainstCurrentDefinitions`
  is the separate §9 signature-mismatch check. Custom blocks (§9) have no real
  workshop/persistence UI yet (Milestone 9) — the compiler is built against a
  `customBlockLookup` function so it doesn't depend on one, tested via
  `Compiler.resolveCustomBlockCall` against an in-memory fake. Verified via 10 new
  Core.Tests (no browser check applies — see `docs/decisions.md` for why).
- Real request queue (§13/§19, Milestone 5): `SpaceKids.Server/RequestQueue.fs` replaces
  the Milestone 2 `SemaphoreSlim` stub with a priority queue (1–5, aging capped at
  priority 2) drained one item at a time by a `BackgroundService` (`RequestQueue.Worker`).
  Failures are classified per §13: 429 → bounded retry honoring the real `Retry-After`;
  `HttpRequestException` (never reached the server) → bounded retry with backoff; a
  post-send `TaskCanceledException` → surfaced to the caller as `AmbiguousFailure`, never
  auto-retried; HTTP 401 → `agents.server_reset_detected` set and dispatch paused
  (recovery is pasting a fresh token, which calls `RequestQueue.clearServerReset()`);
  repeated 5xx (or exhausted `HttpRequestException` retries) → the item is re-queued
  (not failed) and the queue enters a paused "unreachable" state with growing backoff
  probes. A new "Warteschlange" UI section (`QueueService`/`QueueRemoting.fs`, manual
  "Aktualisieren" refresh) shows pending count, reset/unreachable state, and recent
  events. `SpaceKids.FakeSpaceTraders` gained `POST /_fault/mode` (`429`/`5xx`/`reset`/
  `unreachable`/`drop-after-processing`) so every path above is exercised against real
  HTTP responses in `SpaceKids.IntegrationTests`, not just reasoned about. Verified live
  against the real API first (real header names, real error-body shape, weekly reset
  cadence) before designing the classification logic. Verified in a real browser
  (Playwright): the section renders and the refresh button re-fetches. See
  `docs/decisions.md` for the full design rationale and the test-only hooks
  (`resetForTests`/`dispatchNextForTests`/`setMaxAttemptsForTests`) added so ordering/
  aging/retry-exhaustion tests don't need to race a live background worker.
- Runner on the pure scheduler core (§14/§19, Milestone 6): `SpaceKids.Core/Scheduler/`
  (`Types.fs`/`Step.fs`) is a pure, framework-free `step : Clock -> JobState ->
  SchedulerEvent -> (JobState * Effect list)` core — walks every "free" transition
  (variables, branches, loops, pure expressions) in one call, stopping only at one of
  the 6 in-scope actions (navigate/orbit/dock/extract/buyGood/sellGood), a `Wait`
  block, or completion. `SpaceKids.Server/JobRunner.fs` is the minimal in-memory
  foreground shell (§14's own stand-in for Milestone 7's real persistent shell) that
  turns `Effect`s into real `RequestQueue.enqueue` calls; `JobRemoting.fs`/`JobService`
  expose `startJob`/`step`/`run`/`getStatus` to a new "Programm ausführen" section on
  the same combined page — ship picker, Start/Einzelschritt/Ausführen buttons, a German
  activity log. Per-action ambiguous-failure reconciliation (§13) is real: two explicit
  `JobStatus` hops (`Reconciling` between `AwaitingApiResponse` attempts), per-action
  rules matching §13's table, proven against the fake's `drop-after-processing` fault
  with an explicit "exactly one action landed, not two" assertion. `SpaceTraders/
  Client.fs`/`Types.fs` gained the 6 action methods, `GetShip`, and their response
  types, verified field-by-field against the live OpenAPI spec. See
  `docs/decisions.md` for the full design rationale, two real bugs the integration
  tests caught (a missing position-advance before entering a wait; a `remainingSeconds`
  truncation bug hiding an active cooldown from reconciliation), and why the shell
  polls via repeated `stepOnce` calls rather than sleeping inline inside one call.

## Changed this session

Milestone 6 work: `SpaceKids.Core/Scheduler/{Types.fs,Step.fs}` new;
`SpaceKids.Core/Dsl/{Value.fs,Eval.fs}` new (runtime value type + expression
evaluator); `SpaceKids.Server/{JobRunner.fs,JobRemoting.fs}` new, registered in
`Startup.fs`; `SpaceKids.Client/Main.fs` gained the `JobService` contract and
"Programm ausführen" section; `SpaceKids.SpaceTraders/{Types.fs,Client.fs}` gained the
6 action methods + `GetShip` + response types (and folded `route`/`flightMode` into
`ShipNav` — every endpoint returns the same nav shape); `SpaceKids.FakeSpaceTraders/
App.fs` gained the 6 action endpoints + `GetShip`, mutable ship/agent state (a first —
every prior endpoint was read-only), and a controllable `clock`/`fixedTravelSeconds`/
`fixedCooldownSeconds`/`dropAfterProcessingDelayMs`; 15 new `SpaceKids.Core.Tests/
SchedulerTests.fs`; 4 new `tests/SpaceKids.IntegrationTests/JobRunnerTests.fs` plus a
new `AssemblyInfo.fs` disabling cross-file test parallelization in that assembly
(needed once a second file there started touching the same process-wide singletons).
Everything before that was created in earlier sessions (Milestones 0–5) — see git
history.

## Known issues

- **Hosting model is fragile — read `docs/decisions.md` before touching
  `Startup.fs`/`Index.fs`/Client `Startup.fs`.** The stock Bolero template's unified
  render-mode wiring silently never serves `_framework/blazor.web.js` in this
  environment; this project uses the classic Blazor WASM hosting model instead
  (`UseBlazorFrameworkFiles` + `MapFallbackToBolero`, hand-written bootstrap `<script>`
  tag, no `boleroScript`). A build succeeding is not evidence this still works — verify
  in an actual browser.
- `SpaceKids.Client/Main.fs` is still a single non-routed page combining the Milestone
  0/2/3 spikes (Blockly editor + mutator workshop + SpaceTraders dashboard, all on one
  page). Its persistence and catalog are real now, but there's no routing, no fleet/job/
  mission-control dashboards yet — that's later milestones (real UI structure isn't
  called for until the DSL/job model exists to show something meaningful).
- Most tables in the §12 schema (`programs`, `custom_blocks`, `custom_block_versions`,
  `jobs`, `job_logs`, `ship_locks`, `api_cache`) still exist but are unused — their
  columns are provisional until the milestone that actually writes to them (see
  `docs/decisions.md`). `agents`/`api_tokens`/`request_queue_events` are now live.
- `RequestQueue`'s pending list, server-reset flag, and unreachable-since flag are
  process-wide mutable module state (a deliberate singleton, matching this app's
  single-user/single-process shape) — anything that touches them directly in tests
  must call `RequestQueue.resetForTests()` first/last, see
  `tests/SpaceKids.IntegrationTests/Tests.fs`'s `withQueueTest` helper. `JobRunner`'s
  `jobs` dictionary and `SpaceKids.FakeSpaceTraders.App`'s ship/agent/fault/clock state
  are the same kind of singleton (`JobRunner.resetForTests()`/`App.resetForTests()`) —
  see `JobRunnerTests.fs`'s `withJobTest`/`withPumpedQueue` helpers, and the assembly-
  level `DisableTestParallelization` this now requires (see `docs/decisions.md`).
- Jobs are in-memory only (`JobRunner`'s `ConcurrentDictionary`) — no persistence, no
  ship locks, no `next_wake_at`, no restart survival. That's Milestone 7 scope; the
  `jobs`/`ship_locks` tables stay unused until then. The pure `step` core itself is
  meant to be exactly what Milestone 7 persists, so it shouldn't need restructuring.
- Only 6 actions execute (navigate/orbit/dock/extract/buyGood/sellGood). A compiled
  program referencing any of the other 11 action blocks, the 9 info-read blocks, or a
  custom-block call fails the job with a German message rather than executing —
  `getShipInfo`/`getCargo`/`getFuel`/etc. reads have no runtime behavior yet even
  though the compiler happily compiles them.
- `JobRunner` only ever has one `Frame` per job (`scope = "main"`) — `CallCustomBlock`
  is one of the block types that fails cleanly above. The `Frame`/`PathEntry`/
  `LoopState` shapes support real nested calls already (Milestone 9 scope) so this
  isn't expected to need restructuring, only extending.
- The market fetched is always the agent's own headquarters waypoint, not discovered via
  waypoint traits — a documented simplifying assumption (see `docs/decisions.md`), fine
  for most starting waypoints but a real limitation otherwise.
- Catalog block inputs are plain value sockets (accept any block), not typed
  (Schiff/Wegpunkt/Ware/...) — typed sockets are Milestone 9 scope.
- `docs/06-localization.md` and the other docs listed in `plan.md` §17 don't exist yet —
  they're created as their milestones start.
- No accessor blocks exist yet (§8: "Wegpunkt aus Schiff", "Preis aus Handelsware", ...) —
  §6/§7's 20-block catalog never included them, so `Expr.Accessor` exists in the DSL type
  but nothing compiles into it today.
- Custom-block call compilation uses a placeholder `callCustomBlock`/`customBlockId`
  convention invented for Milestone 4's own testing needs, not the Milestone 0 spike's
  `sk_call_<id>` naming — Milestone 9 defines the real mechanism when it builds the
  actual Blockwerkstatt UI (see `docs/decisions.md`).
- Custom-block argument type-checking is a shallow literal-only heuristic (`Expr` has no
  static type system) — revisit once Milestone 9's real typed inputs exist.
- `lists_getIndex`/`lists_setIndex` only support the common "get/append at a plain index"
  shape — other WHERE modes (FROM_END/FIRST/LAST/RANDOM) aren't compiled yet.

## Next tasks

1. Milestone 7 onward — see `plan.md` §19. Milestone 7 adds the real persistent shell
   around the same pure `step` core (jobs table, ship locks, `next_wake_at`,
   restart/clock-skew catch-up) — `JobRunner.fs`'s in-memory `ConcurrentDictionary` and
   real-time polling loop are exactly the "minimal foreground loop" §14 says stands in
   for that shell, not a design to carry forward as-is.

## Commands

```txt
dotnet build SpaceKids.slnx       Build everything (bundles the Blockly TS seam too)
dotnet test SpaceKids.slnx        Run all tests
dotnet run --project src/SpaceKids.Server --urls http://localhost:5290
                                   Run the app
```

No database reset command yet — delete `src/SpaceKids.Server/spacekids.db*` (and
optionally `src/SpaceKids.Server/backups/`) to reset; migrations reapply from scratch on
the next `dotnet run`.

## Important constraints

(Copied from `plan.md` §18 — these apply from day one, not just once their owning
milestone starts.)

```txt
German child-facing UI; English internals.
Primitive API blocks only.
No direct browser-to-SpaceTraders calls.
All API calls use the global request queue.
Jobs must persist safely.
Never blindly retry uncertain non-idempotent API actions; never
  issue a second physical call while the first might still resolve.
Reconciliation decisions use per-ship signals; credits deltas are
  corroborating evidence only.
The Blockly instances are owned entirely by the TS seam — Elmish only
  ever sees JSON strings crossing that boundary.
Custom-block definitions live in the Blockwerkstatt and the
  custom_blocks tables — never inline in a program's workspace JSON.
Inline DSL expressions are pure; every effectful value is its own
  instruction with a resultTarget.
Custom blocks execute as real function calls with a call stack, not
  inlined at compile time. JobState is stack-based with path positions
  and per-frame locals from Milestone 7 onward, even before
  Milestone 9 introduces actual calls.
A program with an active job is read-only (watch mode); editing
  requires pausing or stopping the job.
Custom block versioning is intentionally not fully enforced yet —
  only a structural mismatch check exists. Do not build full
  upgrade/pinning logic without discussing it first.
The pinned Blockly version is a recorded decision; do not bump it
  casually.
Integration tests run against SpaceKids.FakeSpaceTraders, not the
  live API.
```

Also, specific to this session's findings (not yet in the plan's own list, but just as
load-bearing — see `docs/decisions.md`):

```txt
This project targets net10.0, not net8.0 — deliberate, tied to what's installed here.
This project uses the classic Blazor WASM hosting model, not the .NET 8+ unified
  render-mode model — `boleroScript`/`comp<MyApp>{renderMode=...}`/`MapRazorComponents`
  are the wrong tools here; don't reintroduce them without re-verifying in a browser.
Microsoft.AspNetCore.Components.WebAssembly is pinned directly in the Client project to
  match the WASM SDK pack version — Bolero's own transitive pin is older and breaks the
  WASM boot if left unpinned.
```
