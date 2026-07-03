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

## Changed this session

Everything above was created this session — first commit-worthy state of the repo.
Key files: `SpaceKids.slnx`, all `src/*` and `tests/*` projects, `docs/decisions.md`,
`docs/00-project-map.md`, this file, `README.md`, `TODO.md`.

## Known issues

- **Hosting model is fragile — read `docs/decisions.md` before touching
  `Startup.fs`/`Index.fs`/Client `Startup.fs`.** The stock Bolero template's unified
  render-mode wiring silently never serves `_framework/blazor.web.js` in this
  environment; this project uses the classic Blazor WASM hosting model instead
  (`UseBlazorFrameworkFiles` + `MapFallbackToBolero`, hand-written bootstrap `<script>`
  tag, no `boleroScript`). A build succeeding is not evidence this still works — verify
  in an actual browser.
- `SpaceKids.Client/Main.fs` is still the throwaway Milestone 0 spike page (Save/Load/
  Highlight/Read-only over one workspace) — Milestone 3 replaces it with the real client
  UI. Its persistence backing (`WorkspaceRepository.fs`) is real now, but the page itself
  isn't the product.
- Most tables in the §12 schema (`agents`, `api_tokens`, `programs`, `custom_blocks`,
  `custom_block_versions`, `jobs`, `job_logs`, `ship_locks`, `api_cache`,
  `request_queue_events`) exist but are unused — their columns are provisional until the
  milestone that actually writes to them (see `docs/decisions.md`).
- `docs/04-block-catalog.md`, `docs/06-localization.md`, and the other docs listed in
  `plan.md` §17 don't exist yet — they're created as their milestones start.

## Next tasks

1. Milestone 2: token flow, real SpaceTraders API client, minimal single-lane request
   queue stub (§13's non-negotiable queue principle applies from day one, not just once
   the full queue is built in Milestone 5).
2. Milestone 2: read agent, ships, contracts, waypoints, markets from the real API; grow
   `SpaceKids.FakeSpaceTraders` to cover the endpoints consumed so far and point the
   first integration tests at it.

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
