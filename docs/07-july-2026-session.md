# July 2026 session — domain knowledge

Full record of work from this session (not just the custom-block fixes at the end).
Commits `e217ffc` … `a746e0f` on `main`.

**Context loss:** a later compaction/summary pass retained only the final custom-block
commits. This doc was written from git history and source, not from a truncated summary.

## Session map

| Commit | What |
|--------|------|
| `e217ffc` | Split **Programmieren** tab into **Programme** + **Eigene Blöcke** sub-tabs |
| `fa2a1f5` | Diagnose ship-required programs; clearer optional-ship UX |
| `509c28f` | Remove redundant ship-optional hint from Pilots tab |
| `c8eb983` | Isolate `dotnet test` from the live SQLite database |
| `d9b8d6d` | Keep map markers at constant screen size when zooming |
| `45cf8eb` | Deeper galaxy-map zoom; show all systems when zoomed in |
| `175592c` | New accessor block **System aus Wegpunkt** (`waypointSystemField`) |
| `9436df4` | Fix Blockwerkstatt blank workspace; **real** program simulation |
| `55be071` | Fix Blockwerkstatt blanking when opening a custom block |
| `a945170` | Fix custom-block save when RETURN chains through accessors |
| `401cc09` | Fix `CustomBlockRemoting` save-handler compile error |
| `5bdc63e` | Remove definition toolbox from program workspace; `AGENTS.md` build rules |
| `19c36de` | Seed **Eigene Blöcke** from persistence on load |
| `d4a138e` | No-op custom-block save must not bump version |
| `a746e0f` | This documentation (initial subset only — superseded by this file) |

---

## 1. Programmieren tab split (`e217ffc`)

**Problem:** Program editor and Blockwerkstatt lived in one crowded **Programmieren**
view.

**Change:** `ProgramSubTab` in `Main.fs`:

- `ProgramsSubTab` — program library + open program Blockly editor
- `CustomBlocksSubTab` — custom-block library + Blockwerkstatt workshop

Both sub-tabs live under the top-level `ProgrammierenTab`. Only one sub-panel is
visible at a time (`programSubTabStyle` toggles `display`).

**Consequence:** Blockly workspaces are in **hidden divs** when their sub-tab is off.
That drove the lazy-init / resize work in sections 7–8 below.

---

## 2. Ship requirement diagnosis (`fa2a1f5`, `509c28f`)

**Problem:** Ship selection on the Pilots tab is optional for ship-agnostic programs,
but ship-scoped blocks at the top level still need a ship. Users got unclear errors.

### Static analysis (`Validator.fs`)

Exported helpers (also used by `Step.fs` runtime gates):

| Function | Meaning |
|----------|---------|
| `shipScopedActionTypes` / `shipScopedInfoTypes` | Canonical sets of block types that touch a ship |
| `findShipRequirementAtStart` | First ship-scoped block **outside** any `mitSchiff`/`withShip` body |
| `programRequiresShip` | `findShipRequirementAtStart` is `Some` |

**Key rule:** Work inside `mitSchiff`/`withShip` does **not** count — those ships are
resolved at runtime. Only the optional `sonst`/`else` branch of `mitSchiff` still runs
on the pilot-tab ship.

`purchaseShip` / **Kaufe Schiff** is fleet-scoped, not ship-scoped — a program with
only that action can start without a selected ship.

### Runtime messages (`JobRemoting.fs`)

- **`startJob`:** bilingual error naming the offending block (`req.kind`, `req.blockId`)
  and suggesting: select a ship, wrap in `mitSchiff`, or remove the block.
- **`simulateProgram`:** shorter message pointing at Pilots tab ship picker.

### UI (`509c28f`)

Removed explanatory hint text from Pilots tab after the validator messages were
clear enough on their own.

---

## 3. Test database isolation (`c8eb983`)

**Problem:** `dotnet test` could write into `src/SpaceKids.Server/spacekids.db` when
`SPACEKIDS_DB_PATH` was inherited from a dev shell (`scripts/dev.ps1`), corrupting real
agent/token/program data.

### Three layers of defense

1. **`tests/Directory.Build.props`** — sets per-project env vars:
   - `SPACEKIDS_DB_PATH=$(MSBuildProjectDirectory)\.test-spacekids.db`
   - `SPACEKIDS_BACKUPS_DIR=$(MSBuildProjectDirectory)\.test-backups`

2. **`tests/SpaceKids.TestSupport/Bootstrap.fs`** — `TestDbGuard.EnsureInitialized`
   runs from `TestBootstrap.fs` (compiled first in every test assembly via
   `Directory.Build.props`). Re-asserts the same paths under `testhost`.

3. **`Database.defaultDbPath`** (`Database.fs`) — if running under `testhost` and path
   is not clearly a test file (`.test-spacekids`, `spacekids-test-*`, etc.), redirects
   to `%TEMP%/spacekids-testhost-isolated.db`. Escape hatch:
   `SPACEKIDS_ALLOW_LIVE_DB=1`.

### Test

`defaultDbPath never points at the live dev database during dotnet test` in
`SpaceKids.Server.Tests`.

**Gotcha:** Individual tests that need their own file still use `tempDbPath()` with
GUID names — that pattern is unchanged. The guard protects the **default** path used by
code that calls `Database.defaultDbPath` without an explicit path.

---

## 4. Map UX — constant marker size (`d9b8d6d`)

**Problem:** System and galaxy map dots/ships/labels grew visually when zooming in
because radii were fixed in SVG viewBox units.

**Fix:** `mapMarkerSize zoom screenPixels = screenPixels / zoom` in `Main.fs`.

All marker radii, stroke widths, font sizes, and label offsets go through this helper
for both the **system map** (waypoints/ships) and **galaxy map** (star systems).

**Test:** `mapMarkerSize shrinks viewBox units as zoom increases so dots stay screen-constant`
in `SpaceKids.Client.Tests`.

---

## 5. Galaxy map — deeper zoom and more systems (`45cf8eb`)

**Changes in `Main.fs`:**

| Constant / function | Value / behavior |
|---------------------|------------------|
| `galaxyMapMaxZoom` | `64.0` (was much lower) |
| `galaxyMapNodeBudget zoom` | Stepwise cap: 500 → 1200 → 2500 → 5000 → 10000 → unlimited at zoom ≥ 16 |
| `filterGalaxyMapNodes` | Culls off-screen systems unless in `alwaysInclude` (selected system) |

At high zoom, essentially all systems in the catalog can render. At low zoom, culling
keeps SVG performance acceptable.

**Tests:** galaxy map zoom/budget helpers in `SpaceKids.Client.Tests`.

---

## 6. Accessor: System aus Wegpunkt (`175592c`)

New §8 accessor for waypoint records:

| Blockly type | German label | Record field | Output |
|--------------|--------------|--------------|--------|
| `waypointSystemField` | System aus Wegpunkt | `System` | `String` |

Touched:

- `blocks-catalog.ts` — block definition + toolbox
- `Compiler.fs` — `ACCESSOR_BLOCKS` entry
- `JobRunner.fs` — waypoint record builder
- `docs/04-block-catalog.md`
- Core + integration tests

This block enabled the accessor-chain custom-block save scenario in section 9.

---

## 7. Real program simulation (`9436df4`)

**Replaces** the old client-only `simulateRun` in `blockly-host.ts` (removed — it only
walked `getTopBlocks()[0]` with 700ms highlights, no DSL). See `docs/decisions.md`
for the old behavior; that decision is now **superseded** for the program editor.

### User flow

1. User clicks **Simuliere Ausführung** / **Simulate run** on an open program.
2. `Main.fs` serializes workspace JSON → `JobService.simulateProgram(json, shipSymbol)`.
3. Server compiles + validates, runs ephemerally, returns step trace + log.
4. Client plays back highlights (`SimulationPlaybackTick`, 450ms between steps) and shows
   outcome panel (success/failure, log lines, per-step scope/blockId/detail).

### Server (`JobRemoting.fs` + `JobRunner.fs`)

- **`simulateProgram` remoting handler:** compile → validate → optional ship/token checks
  → `JobRunner.simulateProgram`.
- **`programNeedsRuntime`:** pure programs (only `sk_show_message`, math, etc.) skip
  token/ship/API setup.
- **Ephemeral jobs:** id prefix `__sim__` — never persist to `jobs`/`programs`, never
  take ship locks.
- **`simClockHolder`:** mutable fast-forward clock for `sk_wait`/arrival during sim
  (no real-time sleep).
- **`runSimulationLoop`:** drives `stepOnce`/`tick` until terminal; captures
  `SimulationStep` list (dedupes consecutive identical steps).

### Ship/token gates (simulation)

Same `findShipRequirementAtStart` as `startJob`, but simulation message points at
Pilots tab. Runtime-effectful programs need a logged-in token.

---

## 8. Blockwerkstatt blank workspace fixes (`9436df4`, `55be071`)

**Problem:** Opening the custom-blocks sub-tab or opening a block from the library
showed a blank white Blockly canvas (no toolbox/blocks).

### Root causes

1. Blockly injected into a **zero-size hidden div** before the sub-tab became visible.
2. Blazor re-render could **replace the container DOM node**, leaving a stale workspace
   handle in `workspaces` with no `.blocklySvg`.

### Fix (`blockly-host.ts`)

| API | Role |
|-----|------|
| `deferAfterLayout` | `setTimeout(0)` so layout completes first |
| `remountIfDomLost` | If container has no `.blocklySvg`, `destroyWorkspace` and re-inject |
| `resizeWorkspace` / `resizeWorkspaceNow` | `Blockly.svgResize` + `resizeContents` |
| `ensureWorkspaceReady` | await layout → remount → init → resize (idempotent) |

### F# wiring (`Main.fs`)

- Sub-tab switch (`SwitchProgramSubTab`): `ensureWorkspaceReady` for workshop or open
  program container + `resizeWorkspace`.
- `OpenCustomBlock`: `ensureWorkspaceReady` **before** `loadDefinition` (ordering fix
  in `55be071`).
- `OpenProgram`: same lazy init pattern for program editor.
- Startup no longer eagerly inits Blockly; workspaces init when first shown.

Messages: `WorkshopReady`, `Inited` — no-op completions after JS ready.

---

## 9. Custom-block save — accessor RETURN chains (`a945170`, `401cc09`)

**Problem:** Saving a custom block whose **Ergebnis** / **RETURN** input chained
accessors (e.g. `getShipInfo` → `shipWaypoint` → `getWaypoint` → `waypointSystemField`)
failed compile on save.

### Compiler fix (`Compiler.fs`)

When compiling a custom block body from `sk_custom_block_def`:

1. Compile **BODY** statement chain first.
2. Compile **RETURN** expression into a separate `returnHoisted` buffer.
3. Append `returnHoisted` **after** body instructions.

Previously, hoisted info-reads from RETURN could be ordered before the body, breaking
semantics and failing resolution.

`valueOnlyStatement` path: standalone accessor/info blocks used as statements compile
expr + hoisted reads (needed for accessor chains in the workshop body too).

### Remoting (`CustomBlockRemoting.fs`)

Save path derives signature fresh via `deriveCustomBlockSignature`, then
`resolveCustomBlockCall` against a lookup that includes the in-flight definition —
same pattern as running a program against live definitions.

`401cc09` fixed an F# compile error in the save handler (indentation/`match` structure
after the no-op/validation branches were added).

### Test

`a custom block RETURN chain with waypointSystemField compiles on save` in
`SpaceKids.Core.Tests`.

---

## 10. Toolbox split — program vs Blockwerkstatt (`5bdc63e`)

**Intent:** Definition blocks belong only in the workshop; programs use callers only.

| Category | Program workspace | Blockwerkstatt |
|----------|-------------------|----------------|
| **Eigener Block** (def shell, param get, build record) | Hidden | Shown |
| **Eigene Blöcke** (saved callers) | Shown | Shown |

`buildCatalogToolbox(..., { includeDefinitionCategory })` in `toolbox-de.ts`.
`blockly-host.ts` sets `includeDefinitionCategory: true` only when
`containerId === "blockly-workshop-spike"` (must match `Main.fs` `workshopContainerId`).

---

## 11. Seed **Eigene Blöcke** from persistence (`19c36de`)

**Problem:** After reload/remount, **Eigene Blöcke** was empty until the user saved from
the workshop again.

**Cause:** Callers were only injected via `publishCustomBlockSignature` on save into the
then-open program workspace. Per-container maps cleared on remount.

**Fix:**

- Global caches: `globalCustomBlockToolbox`, `globalDynamicAccessorTypes`.
- New `spaceKids.syncCustomBlocks(json)` — for each saved block, parse stored workshop
  JSON in a temp workspace, `registerSignature`, register accessor types, refresh all
  mounted workspaces.
- `Main.fs`: on `CustomBlocksLoaded`, `syncCustomBlockToolbox` loads each
  `definition_json` and calls JS sync. Runs on app init and after library mutations.

`initWorkspace` calls `applyGlobalCustomBlocksToContainer` so late-mounted workspaces
still get the global list.

---

## 12. No-op save — no version bump (`d4a138e`)

**Rule:** Unchanged workshop JSON must not append a `custom_block_versions` row.

- `CustomBlockRepository.saveVersionIfChanged` — string equality on latest
  `definition_json`.
- `CustomBlockRemoting.save` — early return before compile if loaded JSON equals incoming.
- Test: `saveVersionIfChanged keeps the version when workshop JSON is unchanged`.

**Limitation:** Semantic equality is not normalized (positions/serialization drift still
bumps version). Client still runs `publishCustomBlockSignature` + `LoadCustomBlocks` on
every successful save (harmless idempotent refresh).

---

## 13. Agent build verification (`5bdc63e`, `AGENTS.md`)

When changing Server, Client remoting, or cross-project code:

```txt
dotnet test SpaceKids.slnx   # or at minimum dotnet build
Use && not ;
Do not claim green from scoped Core-only runs alone
```

Canonical repo: `C:\dev\space-traders` (not Grok worktrees).

---

## File index (whole session)

| Area | Primary files |
|------|----------------|
| Sub-tabs + simulation UI | `src/SpaceKids.Client/Main.fs` |
| Blockly host / sync / remount | `src/SpaceKids.Client/Blockly/blockly-host.ts` |
| Toolbox | `src/SpaceKids.Client/Blockly/toolbox-de.ts` |
| Accessor catalog | `src/SpaceKids.Client/Blockly/blocks-catalog.ts` |
| Ship requirement | `src/SpaceKids.Core/Dsl/Validator.fs` |
| Compiler / custom blocks | `src/SpaceKids.Core/Dsl/Compiler.fs` |
| Simulation + ephemeral jobs | `src/SpaceKids.Server/JobRunner.fs` |
| Sim + start remoting | `src/SpaceKids.Server/JobRemoting.fs` |
| Custom block save | `src/SpaceKids.Server/CustomBlockRemoting.fs` |
| Version persistence | `src/SpaceKids.Server/Persistence/CustomBlockRepository.fs` |
| DB path guard | `src/SpaceKids.Server/Persistence/Database.fs` |
| Test isolation | `tests/Directory.Build.props`, `tests/SpaceKids.TestSupport/` |
| Agent rules | `AGENTS.md` |

## Verification

Full solution after session work:

```txt
dotnet test SpaceKids.slnx
```

243 tests (session end). Browser verification of simulation playback and post-reload
**Eigene Blöcke** repopulation was not fully scripted in this session.

## Related docs to update when touching this area

- `docs/05-agent-handoff.md` — "Simuliere Ausführung" bullet still describes old
  `simulateRun` in TS; treat **section 7** here as authoritative.
- `docs/decisions.md` — Milestone 3 `simulateRun` decision superseded by server sim.
- `docs/04-block-catalog.md` — includes `waypointSystemField`.