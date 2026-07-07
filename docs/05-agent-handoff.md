# Current state

## Working

- **Post-roadmap work exists beyond this doc's milestone numbering** — see the
  two entries at the top of "Changed this session" below (a 16-finding
  code-review fix pass + a new `SpaceKids.Client.Tests` project, then a
  Contracts tab). `plan.md`'s milestones (0–13 below) are all still accurate
  as history, just no longer the newest thing that happened.

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
- Full block catalog runs end-to-end (§8/§13/§19, Milestone 9): all 20 SpaceTraders
  blocks now actually execute — Milestone 6 only wired 6 of 11 actions and 0 of 9 info
  blocks. Part A added the 5 remaining actions (survey/deliverContract/acceptContract/
  purchaseShip/refuel) with two new reconciliation-fetch kinds
  (`ReconcileContractState`/`ReconcileFleetState`, for the two actions with no
  ship-local signal — `acceptContract` reconciles via a contract fetch,
  `purchaseShip` via a fleet-count fetch, needing a new `JobState.
  lastKnownFleetShipCount` field). Part B added `Value.VRecord`, a real `Eval.
  Accessor` implementation, a whole new (simpler than actions — no reconciliation,
  ever) info-read scheduler path (`JobStatus.AwaitingInfoResponse`, `Effect.
  QueueInfoRead`), `JobRunner.runInfoRead`'s conversion of API responses into the §8
  records (Schiff/Fracht/Ware/Werft/Schiffstyp/Markt/Handelsware/Auftrag/Wegpunkt),
  and 26 new accessor blocks (a 7th "Zugriffe" toolbox category) compiled by a new
  `Compiler.fs` `ACCESSOR_BLOCKS` table. Full field/record documentation in
  `docs/04-block-catalog.md`. See `docs/decisions.md` for the OpenAPI-spec surprises
  (deliverContract is contract-scoped, purchaseShip is fleet-scoped) and a real
  pre-existing bug found via live verification (every action's start-log message was
  landing *after* its result in the log, since Milestone 6 — an effect-ordering bug,
  now fixed).
- Persistent background jobs (§14/§15/§19, Milestone 7): `SpaceKids.Server/JobRunner.fs`
  is now a real persistent shell over the same pure `Step.step` core (Milestone 6) —
  every tick writes through to the `jobs` table (`Persistence/JobRepository.fs`,
  `JobStateJson.fs` for `FSharp.SystemTextJson`-based serialization of the whole
  `JobState`). A new `SpaceKids.Server/JobScheduler.fs` (`BackgroundService`) resumes
  every non-terminal job on startup — `AwaitingApiResponse`/`Reconciling` jobs recover
  via the exact ambiguous-failure path Milestone 6 already built (an unresolved
  in-flight call is unknown-outcome, so it's ambiguous, never silently rerun);
  `WaitingForArrival`/`WaitingForCooldown` jobs need no special handling since the
  tick loop's plain `until <= now` check already tolerates an arbitrarily overdue
  wait (clock-skew catch-up). Ship locks (`Persistence/ShipLockRepository.fs`) reject
  a second job on an already-locked ship and reclaim orphaned ones via
  check-on-acquire plus a low-frequency sweep (only for locks whose job isn't one of
  the process's own live jobs — the tick loop's per-tick lease refresh is what keeps
  a genuinely active job's lease fresh). The scheduler core (`Scheduler/Types.fs`/
  `Step.fs`) gained `Paused`/`Cancelled` statuses and `pausePending`/`cancelPending`
  flags so a pause/cancel request never abandons an in-flight action — deferred until
  it resolves, applied via `settleOrDefer` at each of the 8 points the job would
  otherwise continue. `JobRemoting.fs`/`JobService` gained `pause`/`resume`/`cancel`/
  `listJobs`; the "Programm ausführen" UI is now a pilot dashboard (multiple
  concurrent jobs, one per ship) with a global watch-mode lock (the shared workspace
  goes read-only while any pilot is active — still only one workspace exists, so
  per-program watch mode isn't meaningful yet). `SpaceKids.FakeSpaceTraders` now
  seeds two independently mutable ships, needed to test ship-lock rejection/reclaim
  at all. See `docs/decisions.md` for the four real bugs found (a schema
  migration-number collision, an insert-order-vs-foreign-key bug, a missing
  `workspaces` row, and an empty-workspace parser crash exposing that
  `Validator.validate` had never actually been wired into the running path) plus a
  fifth (`startJob` using the client-supplied token instead of the stored one).
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

**Note:** everything below predates the two most recent work sessions, which
happened *after* `plan.md`'s roadmap (all milestones) was fully closed out and
`TODO.md` was cleared. Those two sessions aren't milestone-numbered — they're
freeform post-roadmap work. Summarized here since this doc wasn't updated at
the time:

**Session N-1 — 16-finding code review + fix-everything pass.** A 3-agent
parallel review (Core DSL/scheduler, Server persistence/job-running, Client
UI) found 16 real bugs/gaps; all fixed, tested, live-verified, committed in 4
grouped commits. Server: `ShipLockRepository.tryAcquire` had a TOCTOU race
between `readLock`/`upsert` (two concurrent calls for the same ship could both
"win") — fixed with a single-connection `BEGIN IMMEDIATE` transaction;
`refreshLease` used to unconditionally `upsert`, which could resurrect a lock
after a different job legitimately reclaimed the ship — now a plain
conditional `UPDATE ... WHERE ship_symbol = $s AND job_id = $j` (0 rows
affected = silent no-op); `SettingsRepository`'s lazy first-read `INSERT` had
no `ON CONFLICT` and could throw on a startup race — replaced with a shared
`ensureSettingsRow` (`INSERT ... ON CONFLICT(id) DO NOTHING`); `CustomBlockRepository.findUsages`
hardcoded German regardless of locale — now takes a `Locale` param. Core:
`Validator.literalTypeMismatch` checked for an input-type label (`"Zahl"`)
the real compiler/client never actually produce (they emit `"Anzahl"`/`"Number"`/
`"Preisgrenze"`/`"Price limit"`) — the check was dead code, now fixed and
covered by a real test; `Eval.asFloat`'s `VString` branch threw a raw
unlocalized `FormatException` instead of this file's own German `failwith`
style; `Compiler.compileCustomBlockCall` did a redundant second lookup of
data already cached by `resolveCustomBlock`. Client (`Main.fs`): `WatchStatusLoaded`/
`WaypointMarketLoaded`/`WaypointShipyardLoaded` had no correlation id, so a
slow response for a previously-watched job/waypoint could overwrite a faster,
newer one — all three now carry the requested id/symbol and guard against it
having changed since dispatch; `ToggleReadOnly`/`ReadOnlyToggled` re-negated
`model.readOnly` at completion time instead of committing the value actually
sent to JS (a stale double-toggle bug); `StartProgram` used the
error-swallowing `Cmd.OfAsync.perform`, so an exception left `startingJob = true`
forever, permanently disabling the Start button — switched to
`Cmd.OfAsync.either` with a real failure handler (this is now the house style —
**never use `Cmd.OfAsync.perform` for a remote call that can fail**, only for
calls that genuinely can't). Also new: `tests/SpaceKids.Client.Tests/` (first
test project for the Client), covering `Main.fs`'s previously-untested pure
helpers (`computeMapBounds`, `scaleMapPoint`, `interpolatedShipPosition`,
`pilotName`) — these were made `internal` (from `private`) specifically so
this project can reach them via `[<assembly: InternalsVisibleTo("SpaceKids.Client.Tests")>]`
in `Startup.fs`, without a full Blazor component-testing framework. Also
discovered along the way (not part of the review): the system map already has
real zoom/pan (`mapZoom`/`mapPanX`/`mapPanY`, `MapWheel`/`MapDragStart`/`ResetMapView`
messages, `on.wheel`/`on.mousedown` in `Main.fs` — Bolero does support these
DOM events). Went from 152 → 173 tests.

**Session N — Contracts tab.** Contracts were previously an afterthought: a
single flat unstyled line inside the Galaxie tab's `viewDashboard`, and
`Contract` (`SpaceKids.SpaceTraders/Types.fs`) didn't model `terms`
(deliver-goods/payment/deadline) at all — those real-API fields were
explicitly documented as "ignored." Now: `Contract` gained `terms: ContractTerms`
(`{ deadline; payment: ContractPayment; deliver: ContractDeliverGood list }`)
and `deadlineToAccept: string option`, matching the real schema field names
exactly (STJ's case-insensitive matching just works, same as everywhere else
in this file). New standalone `AgentRemoting.acceptContract` function (same
"plain function, not inlined into the handler" pattern as
`fetchWaypointMarket`/`fetchWaypointShipyard`, for independent testability),
wired into `AgentService`/`AgentRemoteHandler`. New client `ContractsTab`:
`viewContracts` shows each not-yet-fulfilled contract as a card (deliver
goods with units-fulfilled/required, payment, deadline, an Accept button
disabled while in flight via `acceptingContractId: string option` — the same
in-flight-guard pattern as `startingJob`), with fulfilled contracts collapsed
under a "Verlauf"/history toggle (`contractsHistoryExpanded: bool`). The
active/fulfilled split is a pure `partitionContracts` helper (`internal`, unit
tested in `SpaceKids.Client.Tests`, same convention as the map-math helpers
above). `SpaceKids.FakeSpaceTraders` now seeds two contracts —
`fake-contract-1` (already accepted, exercises the "active, in-progress"
display) and `fake-contract-2` (not yet accepted, exercises the Accept
button). **Gotcha found while live-verifying this**: launching
`SpaceKids.Server` with `--no-launch-profile` (to point it at the fake server
via env vars) also skips `ASPNETCORE_ENVIRONMENT=Development`, which the
launch profile normally sets — without it, static web assets 404
(`staticwebassets.development.json` vs. the production manifest) and the WASM
app never boots. Always set `ASPNETCORE_ENVIRONMENT=Development` explicitly
alongside `SPACETRADERS__BaseUrl`/`SPACEKIDS_DB_PATH`/`SPACEKIDS_BACKUPS_DIR`
when doing this. 181 tests total.

Milestone 13 work (four independent parts, each shipped/verified separately):
Part A — `Compiler.fs`'s `CompileState` gained a `locale: Locale` field;
`compileWorkspace`/`resolveCustomBlockCall`'s public signatures gained a
`locale` parameter; all 7 of its own message sites (no-output/malformed-
accessor/unknown-block-type ×2/missing-input/cycle/not-found) now translate
like `Validator.fs`'s already did. `CustomBlockRemoting.fs` gained a
`currentLocale()` helper (mirroring `JobRemoting.fs`'s own) and its `delete`
refusal message is now bilingual too. ~16 test call sites updated to pass
`De`; 2 new English-error tests. Part B — every `blocks-catalog.ts` catalog/
accessor block input and output, plus `blocks.ts`'s primitive/custom-block-
caller sockets, now carries a real Blockly `.setCheck(...)` type (`"Number"`/
`"String"`/`"Boolean"`/`"List"`, or a synthetic record-shape check like
`"ShipRecord"`/`"MarketRecord"` for accessor `TARGET` inputs and info-block
outputs); `sk_param_get` recomputes its own check live via `onchange`
(mirroring an existing dynamic-state pattern in the same file) since its
meaning depends on which dropdown option is selected. A `TYPE_LABEL_TO_CHECK`
map handles a persisted custom-block `typeLabel` in either German or English
(Milestone 12 never retroactively translates it). Live-verified: loading a
hand-built mismatched connection throws `"Connection checks failed"` (Blockly
refuses it outright), a correctly-typed connection still loads and connects.
Part C — `JobRepository.listHistory` (new) reads the most-recent-50 terminal
jobs straight from the `jobs` table (joined through `programs.workspace_id`
to `program_definitions.name` for a real program name), surviving a restart
unlike `JobRunner.fs`'s in-memory dictionary; `JobService` gained a matching
`listHistory`/`JobHistoryDto`; `Main.fs` gained a `LoadJobHistory`/
`JobHistoryLoaded` message pair and a new "Verlauf"/"History" section below
the logbook. Live-verified across an actual server restart. Part D — a pure
`pilotName (shipSymbol)` function (a stable char-sum hash, not
`String.GetHashCode`, which is per-process-randomized) picks a name from a
small shared name pool; the pilot card's header line (`pilotShipLine` renamed
to `pilotNameLine`) now reads e.g. "🤖 Pilot Finn steuert Schiff FAKE-AGENT-2".
Live-verified the same ship shows the same name across a page reload. 1 new
`SpaceKids.Server.Tests` case (`listHistory` ordering/filtering/name-join); no
new test for Part D (cosmetic, live-verified instead per the plan). 122 tests
total, all green.

Milestone 9 work: `SpaceKids.Core/Scheduler/{Types.fs,Step.fs}` gained the 5 new
action cases, 2 new reconciliation-fetch effect/result pairs, `AwaitingInfoResponse`/
`QueueInfoRead`/`InfoOk`, and `JobState.lastKnownFleetShipCount`; `SpaceKids.Core/Dsl/
{Value.fs,Eval.fs}` gained `VRecord` and a real `Accessor` implementation;
`SpaceKids.Core/Dsl/Compiler.fs` gained an `ACCESSOR_BLOCKS` table; `SpaceKids.
SpaceTraders/{Types.fs,Client.fs}` gained the 5 new action methods + `GetContract`/
`GetShipyard` + response types (verified against the live OpenAPI spec) plus
`Market.tradeGoods`; `SpaceKids.FakeSpaceTraders/App.fs` gained the matching
endpoints, a mutable `contracts` map, and a `tradeGoods` market fixture;
`SpaceKids.Server/JobRunner.fs` gained the 5 new `runAction` arms, `runInfoRead`
(record conversion for all 9 info types), and effect handlers for the 2 new
reconciliation kinds + `QueueInfoRead`; `SpaceKids.Server/JobRemoting.fs` now fetches
the agent (for `lastKnownFleetShipCount`) alongside the ship at job start;
`SpaceKids.Client/Blockly/blocks-catalog.ts` gained 26 accessor blocks + a "Zugriffe"
toolbox category in `toolbox-de.ts`; `SpaceKids.Client/Main.fs` gained a German label
for the new `AwaitingInfoResponse` status. Fixed a real pre-existing bug (Milestone 6,
found via this session's live verification): every action's `awaiting` helper emitted
`[QueueApiCall; LogMessage(startText)]` — since effects apply strictly in order and
`QueueApiCall` recursively drives the job to its next settled state first, the start
message always landed *after* the actual result in the log. Fixed by reordering to
`[LogMessage; QueueApiCall]` everywhere the pattern appeared (including the 3
post-reconciliation retry sites). 25 new `SpaceKids.Core.Tests` cases (10 Part A
happy-path/reconciliation, 6 Part B info-read/accessor, plus compiler/eval tests in
`Tests.fs`); 6 new `SpaceKids.IntegrationTests/JobRunnerTests.fs` cases (one
drop-after-processing reconciliation test per new action, one accessor-chain
end-to-end test) — 86 tests total, all green. Full rationale in `docs/decisions.md`.

Milestone 7 work: migration `0003_jobs_and_locks.sql` new (`0002` was already taken by
Milestone 5's request-queue migration); `SpaceKids.Server/Persistence/{ProgramRepository.fs,
JobRepository.fs,ShipLockRepository.fs,JobStateJson.fs}` new; `SpaceKids.Server/
JobScheduler.fs` new (registered in `Startup.fs`); `SpaceKids.Server/JobRunner.fs`
rewritten (write-through persistence, ship-lock acquire/reclaim, pause/resume/cancel,
`recoverJob`/`pauseOrphan`, `listJobs`); `SpaceKids.Server/JobRemoting.fs` rewritten
(pause/resume/cancel/listJobs, stored-token lookup for `startJob`, `Validator.validate`
now actually wired in, saves the workspace before compiling); `SpaceKids.Core/
Scheduler/{Types.fs,Step.fs}` gained `Paused`/`Cancelled`/pending-flag handling and
`PauseRequested`/`ResumeRequested`/`CancelRequested`; `SpaceKids.Core/Dsl/
BlocklyJson.fs` fixed to treat a missing top-level `"blocks"` section as zero blocks
rather than crashing; `SpaceKids.FakeSpaceTraders/App.fs` ship state keyed by symbol
(two seeded ships); `SpaceKids.Client/Main.fs` pilot dashboard (generalized from a
single `activeJobId`/`jobStatus` to a `pilots` list) and global watch-mode lock; 10 new
`SpaceKids.Core.Tests/SchedulerTests.fs` cases; 5 new `tests/SpaceKids.IntegrationTests/
JobRunnerTests.fs` cases. Full milestone rationale (including the 5 real bugs found) in
`docs/decisions.md`.

Milestone 6 work (prior session): `SpaceKids.Core/Scheduler/{Types.fs,Step.fs}` new;
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
- `jobs`/`programs`/`ship_locks` are real and live as of Milestone 7 (see
  `docs/decisions.md`); `custom_blocks`/`custom_block_versions`/`api_cache` are still
  unused — their columns are provisional until the milestone that actually writes to
  them. `job_logs` also stays unused: the German activity log is part of the
  `JobState` JSON blob in `jobs.execution_state_json`, not a separate table — no
  milestone has needed to query log lines independently of their job yet.
- `RequestQueue`'s pending list, server-reset flag, and unreachable-since flag are
  process-wide mutable module state (a deliberate singleton, matching this app's
  single-user/single-process shape) — anything that touches them directly in tests
  must call `RequestQueue.resetForTests()` first/last, see
  `tests/SpaceKids.IntegrationTests/Tests.fs`'s `withQueueTest` helper. `JobRunner`'s
  `jobs` dictionary and `SpaceKids.FakeSpaceTraders.App`'s ship/agent/fault/clock state
  are the same kind of singleton (`JobRunner.resetForTests()`/`App.resetForTests()`) —
  see `JobRunnerTests.fs`'s `withJobTest`/`withPumpedQueue` helpers, and the assembly-
  level `DisableTestParallelization` this now requires (see `docs/decisions.md`).
- Only 6 actions execute (navigate/orbit/dock/extract/buyGood/sellGood). A compiled
  program referencing any of the other 11 action blocks, the 9 info-read blocks, or a
  custom-block call fails the job with a German message rather than executing —
  `getShipInfo`/`getCargo`/`getFuel`/etc. reads have no runtime behavior yet even
  though the compiler happily compiles them.
- ~~Watch mode (Milestone 7) is global, not per-program~~ — fixed by the saved/named
  multiple-program library (Milestone 11/Part E): a pilot flying one program no
  longer locks a *different* open program, only its own.
- ~~Pilot cards have no name/mission framing (§15's "Pilot Max" flavor)~~ — fixed
  by Milestone 13/Part D: a pure `pilotName (shipSymbol)` function (a stable
  char-sum hash into a small fixed name pool, `Main.fs`) picks a name that's
  the same for a given ship across restarts and re-runs — no new persisted
  state, no name field exists in the real SpaceTraders API data.
- ~~`JobRunner.listJobs()` only returns what's currently loaded in memory ...
  there's no job-history browser yet~~ — fixed by Milestone 13/Part C:
  `JobRepository.listHistory`/`JobService.listHistory` reads the most-recent-50
  terminal jobs straight from the persisted `jobs` table (which never deletes
  rows), so a finished run still shows up in the dashboard's new "Verlauf"/
  "History" section after a restart, even though `JobRunner.fs`'s in-memory
  dictionary itself still forgets terminal jobs on restart exactly as before.
- Ship-lock lease duration (90s) and the scheduler's tick/sweep intervals (1s/60s) are
  hardcoded constants in `JobRunner.fs`/`JobScheduler.fs`, not configurable.
- `CallCustomBlock` executes as a real call (Milestone 9/Part A): `JobState.stack`
  pushes/pops a `Frame` per call, with arguments bound and the return value
  evaluated against the callee's own locals once its position is exhausted.
- The market fetched is always the agent's own headquarters waypoint, not discovered via
  waypoint traits — a documented simplifying assumption (see `docs/decisions.md`), fine
  for most starting waypoints but a real limitation otherwise.
- ~~Catalog block inputs are still plain value sockets (accept any block), not
  typed ... nothing stops plugging the wrong kind of block into either sort of
  socket other than a German runtime error message once it actually runs~~ —
  fixed by Milestone 13/Part B: every catalog/primitive/accessor block input and
  output now carries a real Blockly `.setCheck(...)` type (`"Number"`,
  `"String"`, `"Boolean"`, `"List"`, or a synthetic record-shape check like
  `"ShipRecord"`/`"MarketRecord"`), so Blockly itself refuses a mismatched
  connection at edit time (confirmed live: loading a hand-built mismatched
  connection throws `"Connection checks failed"` rather than silently
  accepting it). `Validator.fs`'s `literalTypeMismatch` server-side backstop is
  unchanged — the two checks are complementary, not a replacement.
- `docs/06-localization.md` and the other docs listed in `plan.md` §17 don't exist yet —
  they're created as their milestones start.
- The `callCustomBlock`/`customBlockId` convention Milestone 4 invented for its own
  testing needs turned out to be exactly the real mechanism — Milestone 9/Part C's
  client-side caller block was built to match it, not the other way around.
- `Validator.fs`'s server-side custom-block argument type-checking is still a
  shallow literal-only heuristic (`Expr` has no static type system) — it only
  catches a literal plugged directly into a typed argument. Milestone 13/Part B
  now also gives each custom-block call's argument sockets a Blockly `.setCheck`
  derived from the same `typeLabel` mutator data, which prevents most mismatches
  at edit time, but a variable/temp/accessor reference of the wrong shape (not a
  literal) can still slip past both checks — a real static type system remains
  out of scope (see `docs/decisions.md`).
- `lists_getIndex`/`lists_setIndex` only support the common "get/append at a plain index"
  shape — other WHERE modes (FROM_END/FIRST/LAST/RANDOM) aren't compiled yet.

- Every `JobRunner.fs` queue call now threads a real `priority: int` (Milestone
  10/Part A) instead of a single hardcoded tier — `JobScheduler.tickOnce`'s
  background sweep uses `JobRunner.backgroundPriority` (3), distinct from a
  player's own interactive step/run (1). Any *new* call site added to
  `JobRunner.fs` should pick the correct tier deliberately, not default back to
  a hardcoded `1`.
- The real SpaceTraders API does not reject unaffordable purchases — credits can
  go negative (`"can be negative if funds have been overdrawn"`, per the OpenAPI
  spec). Don't build "insufficient funds" error handling on the assumption that
  it rejects; it doesn't, confirmed by checking the spec directly (Milestone 10).
- `Waypoint` now carries `traits: WaypointTrait list` (entity inspector feature)
  — the real `ListWaypoints` response already includes this, no new API call
  needed to read it. `SpaceKids.SpaceTraders/Types.fs`'s `Waypoint.systemSymbolOf`
  is the one shared place for deriving a system symbol from a waypoint symbol —
  reuse it rather than re-deriving the same `SYSTEM-WAYPOINT` split logic a
  fourth time.
- The fake's market/shipyard endpoints now 404 for a waypoint without the
  matching trait (previously answered unconditionally for any waypoint) — if a
  test needs market/shipyard data for a *new* fixture waypoint, give it the
  right trait first or it'll 404 exactly like the real API would.
- `AgentService`'s `getWaypointMarket`/`getWaypointShipyard` are lazy
  (button-triggered from the inspector), not fetched automatically — this was a
  deliberate choice, not an oversight; don't "fix" it to eager-load without
  checking with the user first.
- Saved/named multiple-program library (Milestone 11): `program_definitions`
  (new table, 1:1 with its own `workspaces` row by shared id) replaces the
  hardcoded `"blockly-spike"` workspace. `model.containerId` *is* the open
  program's own database id — no separate DOM-id-to-DB-key mapping exists, so
  the pre-existing Speichern/Laden messages needed zero changes. In-page view
  switch (list ↔ open editor), no real Bolero routing yet, matching the
  custom-block library's own list/workshop toggle. `JobState`/`JobSummaryDto`
  gained `programId` so per-program watch mode (Part E) can filter pilots by
  the program they're actually flying, not "any pilot anywhere."
  `Validator.revalidateAgainstCurrentDefinitions` (built in Milestone 9, never
  called) now has its real call site: `ProgramRemoting.fs`'s `loadDefinition`
  compares a reopened program's last compiled snapshot against live
  custom-block definitions and surfaces mismatches as a dismissible banner.
- Bilingual support (Milestone 12): a single `app_settings` row (`getLocale`/
  `setLocale`, `SettingsService`) picks German or English for everything a
  player sees — Blockly's own chrome, every catalog/custom block label and
  tooltip, all `Main.fs` UI text (a `Strings` record with `de`/`en` values,
  not a stringly-typed lookup — a missing translation is a compile error),
  and most server-side error messages. ~~`Compiler.fs`'s own compile-time
  errors ... are not covered — still German only~~ — fixed by Milestone
  13/Part A: `CompileState` gained a `locale` field, threaded through
  `compileWorkspace`/`resolveCustomBlockCall`'s public signatures, and all 7
  of `Compiler.fs`'s own message sites now translate like `Validator.fs`'s
  already did. The DSL's
  `VRecord` field keys used to literally be the German display word
  (`"Frachtkapazität"`) — Milestone 12 made them canonical English keys
  (`"CargoCapacity"`) decoupled from whichever language the accessor block's
  own label renders in; if you're adding a new §8 accessor block, follow that
  convention (English key in `Compiler.fs`'s `ACCESSOR_BLOCKS` and
  `JobRunner.fs`'s record builders, translated label only in
  `blocks-catalog.ts`).

## Known limitations

(The four limitations Milestone 13 closed — `Compiler.fs`'s German-only
compile errors, untyped Blockly sockets, the vanishing-after-restart job
dashboard, and nameless pilot cards — are no longer listed here; see the
struck-through bullets in "Known issues" above for what replaced each one.)

## Next tasks

0. **Current/upcoming**: a "Flotilla" feature (multi-ship program management) is
   being planned as of this doc's last update — see `plan.md`/a dedicated
   milestone doc if one exists by the time you read this, or ask the user if
   not. Relevant context if you're picking this up: today, every ship-scoped
   DSL block (`navigate`/`orbit`/`dock`/`extract`/`refuel`/etc.) has **no**
   ship-symbol input at all — the compiler never threads a ship symbol
   through a compiled action (`Compiler.fs`), and the scheduler binds a job to
   exactly one ship (`JobState.shipSymbol: string option`) at job-creation
   time, injecting it implicitly at every dispatch site in `Step.fs`. A
   program today is thus written "ship-agnostically" and only becomes
   ship-specific when a pilot is picked to run it. Flotilla means changing
   that 1:1 job-to-ship binding into something that can address multiple
   ships from one program — this is a real architectural change to
   `JobState`/`Step.fs`/the compiler/the block catalog, not a small addition.
1. plan.md's roadmap (§19) has nothing outstanding: Milestones 9 (custom
   reusable blocks, §9), 10 (fleet mode), 11 (saved/named multiple-program
   library), 12 (bilingual support), and 13 (compiler translation, block
   type-checking, job history, pilot flavor) are all done. Milestone 9: real
   call-stack execution, persistence, typed inputs/structured outputs, the
   Blockwerkstatt UI, and cross-view highlighting. Milestone 10: queue
   priority differentiation (background vs. interactive), a fleet-level
   Logbuch, and a test proving concurrent-pilot reconciliation doesn't
   cross-contaminate. Milestone 11: a real program library
   (create/open/rename/delete), per-program watch mode, and the
   structural-mismatch check's first real call site. Milestone 12: a
   runtime-switchable German/English UI, block catalog, and (mostly)
   server-side error messages. The entity inspector + visual system map
   (plan.md's own "later idea," grown into a real drill-down feature per the
   user's own redirect) is also done: waypoint traits, on-demand
   market/shipyard, a ship/waypoint inspector with full cross-navigation, and
   an SVG map with clickable, auto-refreshing markers. Milestone 8 ("first
   missions") was removed from the roadmap entirely, not deferred — see
   `docs/decisions.md`. Any further work now comes from known limitations
   logged elsewhere in this doc, not an existing roadmap item.

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
  inlined at compile time. JobState has been stack-based with path
  positions and per-frame locals since Milestone 7 (forward-designed
  for this); Milestone 9/Part A made CallCustomBlock actually
  push/pop a frame.
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
