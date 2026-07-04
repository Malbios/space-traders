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
    JobRunner.fs               In-memory foreground shell driving Scheduler/Step.step
                               (§14, Milestone 6) — turns Effects into real
                               RequestQueue.enqueue calls; startJob/stepOnce/
                               runToCompletion/getStatus
    WorkspaceRemoting.fs       Bolero remote service backing the spike page's save/load,
                               now via Persistence/WorkspaceRepository.fs
    AgentRemoting.fs           Bolero remote service for the SpaceTraders dashboard —
                               every call routed through RequestQueue.fs
    QueueRemoting.fs           Bolero remote service backing the "Warteschlange" status UI
    JobRemoting.fs             Bolero remote service backing "Programm ausführen" —
                               compiles the workspace JSON server-side, drives JobRunner
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
                                     ApiResult (§14, Milestone 6) — no SpaceTraders
                                     dependency, deliberately (see docs/decisions.md)
      Step.fs                       The pure `step` core — walks free transitions,
                                     stops at the 6 in-scope actions, reconciles
                                     ambiguous failures (§13)
  SpaceKids.SpaceTraders/   SpaceTraders API client (Types.fs, Client.fs) — verified
                             field-by-field against the real OpenAPI spec; gained the 6
                             action methods + GetShip in Milestone 6
  SpaceKids.FakeSpaceTraders/  In-process fake API (§13a) — App.fs (endpoints) +
                             Program.fs (testable entry point) for deterministic tests;
                             mutable ship/agent state + the 6 action endpoints since
                             Milestone 6
tests/
  SpaceKids.Core.Tests/
    SchedulerTests.fs            Pure step-core tests (Milestone 6) — fake clock, zero
                                   DB/network
  SpaceKids.Server.Tests/
  SpaceKids.IntegrationTests/   Runs SpaceTradersClient against SpaceKids.FakeSpaceTraders
    AssemblyInfo.fs               DisableTestParallelization — every test here touches
                                   process-wide singleton state (RequestQueue/JobRunner/
                                   App), so cross-file parallel runs aren't safe
    JobRunnerTests.fs             JobRunner end-to-end against the fake (Milestone 6)
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
  pure `step` core; `JobRunner.fs` in-memory foreground shell; 6 real actions
  (navigate/orbit/dock/extract/buyGood/sellGood) with per-action ambiguous-failure
  reconciliation (§13); "Programm ausführen" UI (ship picker, Start/Einzelschritt/
  Ausführen, German activity log). No persistence yet — in-memory jobs only,
  Milestone 7 scope. See `docs/decisions.md`.
- **Milestone 7** onward: not started.
