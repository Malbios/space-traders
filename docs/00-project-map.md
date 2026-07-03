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
    Main.fs                  Elmish app (currently: Milestone 0 spike page only)
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
      Backup.fs                     Hourly VACUUM INTO + retention (BackgroundService)
    WorkspaceRemoting.fs       Bolero remote service backing the spike page's save/load,
                               now via Persistence/WorkspaceRepository.fs
  SpaceKids.Core/           Domain, DSL, validation, scheduling (framework-free, per §14)
  SpaceKids.SpaceTraders/   SpaceTraders API client
  SpaceKids.FakeSpaceTraders/  In-process fake API (§13a) for deterministic tests
tests/
  SpaceKids.Core.Tests/
  SpaceKids.Server.Tests/
  SpaceKids.IntegrationTests/   Runs against SpaceKids.FakeSpaceTraders
docs/
  decisions.md              Hard-to-reverse calls and why
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
- **Milestone 2 (real data, no Blockly yet)** onward: not started. `Main.fs`'s spike page
  is still throwaway — Milestone 3 replaces the client UI.
