# Current state

## Working

- Full solution scaffold: `SpaceKids.Client` (Bolero WASM), `SpaceKids.Server` (ASP.NET
  Core host), `SpaceKids.Core`, `SpaceKids.SpaceTraders`, `SpaceKids.FakeSpaceTraders`,
  plus `Core.Tests`/`Server.Tests`/`IntegrationTests`. Builds clean with `dotnet build
  SpaceKids.slnx`, no warnings.
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
- Everything under `SpaceKids.Client/Main.fs` and `SpaceKids.Server/Persistence.fs` /
  `WorkspaceRemoting.fs` is throwaway Milestone 0 spike code (a single hardcoded
  `spike_workspaces` SQLite table, no migrations, no WAL/busy_timeout). Milestone 1
  replaces the persistence layer with the real schema (§12); Milestone 3 replaces the
  client UI. Don't build on top of the spike table — replace it.
- No CI wiring yet beyond `dotnet build`/`dotnet test` working locally (see Commands
  below) — no GitHub Actions workflow file exists yet.
- `docs/04-block-catalog.md`, `docs/06-localization.md`, and the other docs listed in
  `plan.md` §17 don't exist yet — they're created as their milestones start.

## Next tasks

1. Milestone 1: real SQLite schema (§12) — `workspaces`, `programs`, `jobs`, etc. tables,
   WAL mode + `busy_timeout` pragma, migrations, the hourly `VACUUM INTO` backup task.
   Delete the spike's `spike_workspaces` table/`Persistence.fs` once superseded.
2. Milestone 1: CI build+test workflow (a `.github/workflows/ci.yml` or equivalent) that
   runs `dotnet build` and `dotnet test` on a fresh checkout — prove the npm/esbuild
   MSBuild wiring in `SpaceKids.Client.fsproj` works without any manual setup step.
3. Milestone 2: token flow, real SpaceTraders API client, minimal single-lane request
   queue stub (§13's non-negotiable queue principle applies from day one, not just once
   the full queue is built in Milestone 5).

## Commands

```txt
dotnet build SpaceKids.slnx       Build everything (bundles the Blockly TS seam too)
dotnet test SpaceKids.slnx        Run all tests
dotnet run --project src/SpaceKids.Server --urls http://localhost:5290
                                   Run the app
```

No database reset command yet — the spike's SQLite file is
`src/SpaceKids.Server/spacekids.spike.db`; delete it to reset. Real migrations start in
Milestone 1.

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
