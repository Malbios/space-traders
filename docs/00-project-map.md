# Project map

Where things live and what to read first.

## Read before a coding session

1. `README.md` — commands, prerequisites.
2. `TODO.md` — outstanding tasks.
3. `docs/05-agent-handoff.md` — current state, what's known-broken, next tasks.
4. `docs/decisions.md` — hard-to-reverse calls already made; don't relitigate without
   reading why first.
5. `plan.md` — the full build plan (product goals, architecture, all milestones). Long;
   grep for the section you need (§-numbers referenced throughout code comments and
   other docs map to this file's headings).

## Solution layout

```txt
SpaceKids.slnx
src/
  SpaceKids.Client/        Bolero WASM client
    Blockly/                 The TS seam (§3a) — sole owner of every Blockly instance
      blocks.ts                 Milestone 0 spike blocks (sk_show_message, sk_wait) +
                                 the custom-block mutator spike (§9 mini-spike)
      blocks-catalog.ts          The real 20-block German catalog (§6/§7, Milestone 3)
      toolbox-de.ts               buildCatalogToolbox — 6 categories, both workspaces
    Main.fs                  Elmish app — Milestone 0/2/3 work all on one non-routed
                               page (Blockly editor + mutator workshop + SpaceTraders
                               dashboard). Real routing/dashboards come with the DSL
                               milestones, not before.
  SpaceKids.Server/         ASP.NET Core host
    Startup.fs                Classic Blazor WASM hosting wiring — see docs/decisions.md
                               before changing this
    Index.fs                  The server-rendered page shell
    Persistence/               Migrations, WAL/busy_timeout, backups (§12) — see
                               docs/decisions.md
      Migrations/0001_initial.sql  Full 12-table core schema
      MigrationRunner.fs            Applies pending migrations, tracked in schema_versions
      Database.fs                   Connection + busy_timeout pragma
      WorkspaceRepository.fs        Real `workspaces` table save/load
      AgentRepository.fs            Real `agents`/`api_tokens` save/load
      Backup.fs                     Hourly VACUUM INTO + retention (BackgroundService)
    RequestQueue.fs            Priority queue + aging + retry classification + server-
                               reset/unreachable handling (§13, Milestone 5); a
                               BackgroundService `Worker` drains it; logs to
                               request_queue_events
    JobRunner.fs               Persistent shell driving Scheduler/Step.step (§14,
                               Milestone 6/7) — write-through cache over the jobs
                               table; startJob/stepOnce/runToCompletion/pause/resume/
                               cancel/listJobs; ship-lock acquire/reclaim, orphan
                               recovery
    JobScheduler.fs            BackgroundService (Milestone 7, §14): resumes every
                               non-terminal job on startup, polls due jobs + refreshes
                               ship-lock leases every tick, low-frequency lease sweep
    Persistence/ProgramRepository.fs   First real write to `programs` (Milestone 7)
    Persistence/JobRepository.fs        Job row insert/update/load (Milestone 7)
    Persistence/ShipLockRepository.fs   Ship-lock acquire/refresh/release/sweep (Milestone 7)
    Persistence/JobStateJson.fs         FSharp.SystemTextJson (de)serialization for
                                          JobState/CompiledProgram (Milestone 7)
    WorkspaceRemoting.fs       Bolero remote service backing the spike page's save/load,
                               now via Persistence/WorkspaceRepository.fs
    AgentRemoting.fs           Bolero remote service for the SpaceTraders dashboard —
                               every call routed through RequestQueue.fs
    QueueRemoting.fs           Bolero remote service backing the "Warteschlange" status UI
    JobRemoting.fs             Bolero remote service backing "Programm ausführen" —
                               compiles/validates the workspace JSON server-side,
                               drives JobRunner; startJob/step/run/getStatus/pause/
                               resume/cancel/listJobs (Milestone 6/7)
  SpaceKids.Core/           Domain, DSL, validation, scheduling (framework-free, per §14)
    Dsl/
      Types.fs                   The DSL itself (§10) — Expr, Instruction, CompiledProgram
      Value.fs                     Runtime value type (Milestone 6) for evaluating Expr
      Eval.fs                       Pure expression evaluator, used by Scheduler/Step.fs
      BlocklyJson.fs               Parses Blockly's serialized workspace JSON
      Compiler.fs                   Blockly workspace -> DSL, expression linearization
      Validator.fs                  Static checks (§11) + the §9 signature-mismatch check
    Scheduler/
      Types.fs                     JobState/Frame/PathEntry/Effect/SchedulerEvent/
                                     ApiResult (§14, Milestone 6/7) — Paused/Cancelled
                                     statuses and pause/cancel-pending flags added in
                                     Milestone 7; no SpaceTraders dependency,
                                     deliberately (see docs/decisions.md)
      Step.fs                       The pure `step` core — walks free transitions,
                                     stops at the 6 in-scope actions, reconciles
                                     ambiguous failures (§13), handles pause/resume/
                                     cancel (Milestone 7)
  SpaceKids.SpaceTraders/   SpaceTraders API client (Types.fs, Client.fs) — verified
                             field-by-field against the real OpenAPI spec; gained the 6
                             action methods + GetShip in Milestone 6
  SpaceKids.FakeSpaceTraders/  In-process fake API (§13a) — App.fs (endpoints) +
                             Program.fs (testable entry point) for deterministic tests;
                             mutable ship/agent state + the 6 action endpoints since
                             Milestone 6; ship state keyed by symbol (two seeded ships)
                             since Milestone 7, for ship-lock testing
tests/
  SpaceKids.Core.Tests/
    SchedulerTests.fs            Pure step-core tests (Milestone 6/7) — fake clock,
                                   zero DB/network; pause/resume/cancel, restart
                                   recovery, clock-skew added in Milestone 7
  SpaceKids.Server.Tests/
  SpaceKids.IntegrationTests/   Runs SpaceTradersClient against SpaceKids.FakeSpaceTraders
    AssemblyInfo.fs               DisableTestParallelization — every test here touches
                                   process-wide singleton state (RequestQueue/JobRunner/
                                   App), so cross-file parallel runs aren't safe
    JobRunnerTests.fs             JobRunner end-to-end against the fake (Milestone 6);
                                   ship locks, restart resume, lease sweep, deferred
                                   pause added in Milestone 7
docs/
  decisions.md              Hard-to-reverse calls and why
  04-block-catalog.md        The German block catalog (§6/§7) — consumed by the
                               toolbox build (M3) and will be consumed again by the
                               DSL compiler (M4)
  05-agent-handoff.md        Current state / next tasks (update every session)
  (other docs listed in plan.md §17 are created as their milestones start)
```

## Milestone status

Tracked informally here until the project has enough milestones in flight to need more
structure. See `plan.md` §19 for what each milestone covers.

- **Milestone 0 (toolchain + interop spike): done.** See `docs/decisions.md` for what was
  proven and the hosting-model/version-pin issues that ate most of the time.
- **Milestone 1 (foundation): done.** Real SQLite schema, migrations, WAL/busy_timeout,
  hourly `VACUUM INTO` backups. See `docs/decisions.md`.
- **Milestone 2 (real data, no Blockly yet): done.** Token flow (paste-token), real
  SpaceTraders API client, single-lane request queue stub, grown
  `SpaceKids.FakeSpaceTraders`, verified against the real live API. See
  `docs/decisions.md`.
- **Milestone 3 (Blockly in German, full integration): done.** Full 20-block German
  catalog + `docs/04-block-catalog.md`, 6-category toolbox, save/restore (already
  proven), highlight-during-simulated-run. See `docs/decisions.md`.
- **Milestone 4 (DSL and validation): done.** `SpaceKids.Core/Dsl/` compiles Blockly
  workspace JSON into the internal DSL with expression linearization, and statically
  validates it (§10/§11). Pure library, no UI/Server wiring — see `docs/decisions.md`.
- **Milestone 5 (request queue): done.** Priority + aging queue, definite/ambiguous retry
  classification, 429/server-reset/API-unreachable handling, queue status UI, fault
  injection in `SpaceKids.FakeSpaceTraders`. See `docs/decisions.md`.
- **Milestone 6 (runner on the pure scheduler core): done.** `SpaceKids.Core/Scheduler/`
  pure `step` core; 6 real actions (navigate/orbit/dock/extract/buyGood/sellGood) with
  per-action ambiguous-failure reconciliation (§13). See `docs/decisions.md`.
- **Milestone 7 (persistent background jobs): done.** `JobRunner.fs` is now a real
  persistent shell (write-through cache over the `jobs` table); `JobScheduler.fs`
  (BackgroundService) resumes every non-terminal job on restart (ambiguous-failure
  recovery for anything mid-call, clock-skew catch-up for anything waiting), polls due
  jobs, and refreshes ship-lock leases every tick, with a low-frequency sweep
  reclaiming expired locks. Ship locks (§14) reject a second job on an already-locked
  ship and reclaim orphaned ones. Pause/resume/cancel added to the scheduler core
  (deferred while an action/reconciliation is in flight, never abandoning it). The
  "Programm ausführen" UI is now a pilot dashboard — multiple concurrent jobs, one per
  ship, each with Pause/Fortsetzen/Stoppen — and the shared workspace goes read-only
  (watch mode, global rather than per-program) while any pilot is active. See
  `docs/decisions.md` for the four real bugs found while wiring this up.
- **Milestone 9 (finish the block catalog): done.** All 20 SpaceTraders blocks now
  actually execute — Part A wired the 5 remaining actions (survey/deliverContract/
  acceptContract/purchaseShip/refuel), including two new reconciliation-fetch kinds
  for the two actions with no ship-local signal (contract fetch for acceptContract,
  fleet-count fetch for purchaseShip). Part B built the §8 data model from scratch:
  `Value.VRecord`, a real `Eval.Accessor`, a whole new no-reconciliation-needed
  info-read scheduler path, `JobRunner.runInfoRead`'s conversion of all 9 info blocks
  into flat German records, and 26 new accessor blocks (a 7th "Zugriffe" toolbox
  category). See `docs/decisions.md` for the OpenAPI-spec surprises and a real
  pre-existing log-ordering bug (Milestone 6) found and fixed via live verification.
  Milestone 8 ("first missions") deliberately skipped for now — not the user's
  current priority (see `docs/decisions.md`/TODO.md).
- **Milestone 9 (custom reusable blocks, §9): done.** Real function-call semantics,
  built in five parts. Part A: `JobState.stack` (a `Frame list`, forward-designed
  since Milestone 6) now genuinely pushes/pops on `CallCustomBlock`, binding
  arguments and the return value (`CompiledCustomBlock.returnExpr`) via
  `resultTarget`. Part B: `CustomBlockRepository.fs` — append-only versioning, a
  working delete-usage-check (`findUsages`), and `JobRemoting.fs`'s `lookup`
  finally backed by real persistence instead of `fun _ -> None`. Part C: a real
  typed-input mutator (Schiff/Wegpunkt/Ware/Anzahl/Preisgrenze/Liste), one generic
  `callCustomBlock` caller block type (replacing the Milestone-0 spike's
  `sk_call_<id>` scheme) whose shape is rebuilt per-instance from a signature
  cache, and structured-record outputs (`Expr.RecordLiteral`, an `sk_build_record`
  mutator block, dynamically generated `accessor_<id>_<field>` blocks). Part D: the
  Blockwerkstatt UI — a block library (create/open/rename/delete) and a workshop
  view wired to `CustomBlockRemoting.fs`; saving a workshop derives a fresh
  signature from the just-edited JSON, persists a new version, and re-publishes the
  caller into the main program's toolbox. Part E: `Step.blockIdPerFrame` (one
  `(scope, blockId option)` pair per stack frame, deepest-first) drives an "innen
  aktiv" indicator + "Block öffnen" affordance on the program view while a call is
  in flight. See `docs/decisions.md` for a real bug found during Part E's live
  verification (a frame whose position had already advanced past its own last
  instruction was silently dropped, making "innen aktiv" undetectable in exactly
  that case).
- **Milestone 10 (fleet mode, §13/§14/§15): done.** Most of §19's bullets were
  already satisfied by Milestone 7 (several concurrent pilots, pause/resume/stop);
  three real gaps closed. Part A: every `JobRunner.fs` queue call now threads a
  real `priority: int` instead of a single hardcoded tier — `JobScheduler.tickOnce`
  (fully automatic background driving) now genuinely uses §13's "background job
  action" tier (3), distinct from a player's own interactive step/run (1), for the
  first time since Milestone 6. Part B: a "Logbuch" panel in `Main.fs` aggregating
  every active pilot's last activity line in one place (no schema/remoting
  change — reuses `JobSummaryDto.lastLogLine`). Part C: an integration test proving
  two ships trading concurrently — one deliberately mid-`Reconciling` from an
  ambiguous failure while the other completes a real, credits-changing trade —
  don't cross-contaminate each other's outcome. A fourth planned item
  ("insufficient-credits" friendly error) was dropped after checking the real
  SpaceTraders OpenAPI spec: credits can go negative in the real game, and no such
  error exists to translate — see `docs/decisions.md`.
- **Entity inspector + visual system map: done.** plan.md's "later idea" from
  Milestone 9 grew into a real drill-down inspector once the user redirected
  mid-planning: click a ship to see all its details, click through to the
  waypoint it's at, see traits and every other ship there, load market/shipyard
  data on demand. Part A: `Waypoint` gained `traits: WaypointTrait list` — the
  real API already returns this on the same `ListWaypoints` call, just wasn't
  being deserialized; the fake's fixture gained plausible trait data (HQ:
  MARKETPLACE + SHIPYARD; the asteroid field: a mining trait only). Part B:
  `AgentService` gained `getWaypointMarket`/`getWaypointShipyard` (lazy,
  button-triggered, not automatic), reusing `SpaceTradersClient` directly like
  the rest of the dashboard; the fake's market/shipyard endpoints now 404 for a
  waypoint without the matching trait (previously unconditional) so the "no
  market here" path is actually exercised. Part C: the inspector UI itself —
  `InspectedEntity` selection state, a ship panel (every `Ship` field) and a
  waypoint panel (traits, ships present, gated market/shipyard buttons), full
  cross-navigation between them. Part D: an SVG system map (`viewSystemMap`,
  pure F#/Bolero.Html — no JS interop needed, unlike Blockly) with waypoints as
  colored circles and ships as triangles (interpolated between waypoints for an
  in-transit ship, using real elapsed time against the API's own timestamps),
  both clickable into the same inspector, refreshed automatically via a
  self-rescheduling tick reusing the `WatchTick` pattern.
