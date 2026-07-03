# Decisions

Record of deliberate, hard-to-reverse calls. Update this file when a decision changes;
don't relitigate a listed decision without recording why.

## Runtime: net10.0, not net8.0

Bolero's NuGet packages ship a `lib/net8.0` binary but no `net9.0`/`net10.0` target. All
projects are pinned to **net10.0** (not net8.0), for a reason specific to this dev
environment: only the .NET 10 SDK/runtime is installed here (no net8.0 shared runtime).
A net10.0 project can still reference net8.0-targeted NuGet packages (Bolero,
Bolero.Server, etc.) — ordinary .NET binary/TFM compatibility, not a special case.

Revisit if Bolero ever publishes a net10.0-targeted build, or if this moves to an
environment with the net8.0 runtime actually installed — net8.0 remains the "intended"
target per Bolero's own docs.

## Hosting model: classic Blazor WebAssembly, not the .NET 8+ unified render-mode model

**This took most of a session to pin down — read this before touching `Startup.fs`,
`Index.fs`, or the Client's `Startup.fs` bootstrap.**

The stock `dotnet new bolero-app` template wires the server (`AddRazorComponents()`
`.AddInteractiveWebAssemblyComponents()` + `MapRazorComponents<Index.Page>()
.AddInteractiveWebAssemblyRenderMode()`) for the .NET 8+ "Blazor Web App" unified
render-mode model, using `boleroScript` to emit the bootstrap tag. On this project's
Bolero version (Server 0.24.39, Templates 0.24.18 — the templates package is stale
relative to the library), that combination never serves `_framework/blazor.web.js`
(the script this hosting model needs): reproduced identically on a completely untouched
fresh scaffold, on both net8.0 and net10.0, with every combination of
`UseStaticFiles`/`MapStaticAssets`/catch-all-vs-exact routes tried. Root cause
(confirmed via the generated `staticwebassets.build.endpoints.json` manifest): the
composed `blazor.web.js` asset is sourced from the `Microsoft.AspNetCore.App.Internal.Assets`
package, brought in as an **implicit** dependency of the Client project by the .NET SDK
— and it never appeared in this project's restored dependency graph, unlike a plain
`dotnet new blazor --interactivity WebAssembly` scaffold where it does. The exact SDK
condition that gates this implicit reference was not identified (`StaticWebAssetProjectMode`
was ruled out — it evaluates to `Default` in both cases) — likely something specific to
how `Microsoft.NET.Sdk.BlazorWebAssembly` decides a project participates in the unified
model, which the Bolero template's Client project apparently doesn't trigger.

**Decision: stop fighting it. Use the classic (pre-.NET8) Blazor WebAssembly hosted
model instead**, which needs none of this:

```txt
Server (Startup.fs):
  AddControllersWithViews()   — MapFallbackToBolero renders the page via IHtmlHelper,
                                 which needs MVC's view-rendering services registered
                                 even though this app has no controllers/views.
  AddBoleroComponents(), AddBoleroRemoting<...>()
  app.UseBlazorFrameworkFiles()   — serves the Client's wwwroot output
                                     (blazor.webassembly.js, the WASM payload) at
                                     _framework/* — a real physical file, unlike
                                     blazor.web.js.
  app.UseStaticFiles() / UseRouting() / UseAuthorization()
  app.MapBoleroRemoting()
  app.MapFallbackToBolero(Index.page)

Server (Index.fs):
  Plain `div { attr.id "main" }` — no `comp<MyApp> { attr.renderMode ... }` marker
    (that's unified-model-only).
  Do NOT use `boleroScript` — it hardcodes `_framework/blazor.web.js` regardless of
    hosting model. Write the bootstrap tag by hand instead:
    `script { attr.src "_framework/blazor.webassembly.js" }`.

Client (Startup.fs):
  `builder.RootComponents.Add<Main.MyApp>("#main")` before `builder.Build()` — this is
    what mounts the app into the div in classic mode (the unified model doesn't need
    this; it discovers the root via the server-rendered marker instead).
```

If Bolero.Templates ever republishes with a fixed scaffold (or the implicit-package-reference
gate gets identified), this whole section can be revisited — but don't casually "clean up"
back to `comp<MyApp>`/`boleroScript`/`MapRazorComponents` without re-verifying end to end
in a browser, not just that the build succeeds.

## Client package pin: `Microsoft.AspNetCore.Components.WebAssembly` 10.0.9

Separate, real bug found while chasing the above: Bolero pins its own transitive
dependency on `Microsoft.AspNetCore.Components.WebAssembly` at **8.0.0**, which skews
against the net10.0 WASM runtime/SDK pack (`Microsoft.NET.Sdk.WebAssembly.Pack`, 10.0.9)
actually driving the build. Symptom: the app loads and `blazor.webassembly.js` starts,
but `dotnet.js`'s dynamic module loader throws `Failed to fetch dynamically imported
module: http://localhost:5290/0` and the WASM runtime never boots — a version-skew bug,
not a hosting-model issue. Fixed by adding a direct `<PackageReference
Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.9" />` in
`SpaceKids.Client.fsproj` so NuGet's nearest-wins resolution picks the matching version
instead of Bolero's older pin. Verify this is still necessary (`grep
"microsoft.aspnetcore.components.webassembly/" obj/project.assets.json` should show
10.0.9, not 8.0.0) whenever Bolero or the target TFM changes.

## Blockly version: 13.1.0 (pinned exact)

**Date:** Milestone 0.

Pinned in `src/SpaceKids.Client/package.json` with an exact version (no `^`/`~` range).
Upgrading this is a deliberate decision — re-run the Milestone 0 Part A/C spike checklist
(§3b, §19) when it happens: re-verify where the procedure blocks live and re-run the
mutator/toolbox-regeneration spike below.

### Where do procedure blocks live at 13.1.0? (§3, §9a)

Checked directly against the installed package (`node_modules/blockly`):

- `Blockly.Blocks["procedures_defnoreturn"]`, `procedures_defreturn`,
  `procedures_callnoreturn`, `procedures_callreturn`, etc. are defined in Blockly
  **core** (`blockly/blocks_compressed.js`), not in a separate plugin.
- `@blockly/block-shareable-procedures` still exists as an npm package, but at this
  version its published version number (13.1.0) tracks core's own — it is not needed as
  a dependency for anything this project does. It was **not** installed.
- Conclusion: at 13.1.0 the split described in the plan (procedure blocks partially
  moved into `@blockly/block-shareable-procedures` around Blockly v10) is not the
  relevant risk for this pin. It doesn't matter either way, because per §9a this project
  never uses Blockly's native procedure blocks — its own definition/caller pair
  (`sk_custom_block_def`, `sk_call_<id>`, see `Blockly/blocks.ts`) is built instead,
  specifically so this kind of churn in Blockly's internals is a non-issue.

Note: `blockly/blocks` (the standard block library — `math_number`, `text`, etc.) must
be imported for side effects (`import "blockly/blocks"`) alongside `blockly/core` —
`blockly/core` alone does not register them, and the toolbox references a couple of them
directly (§ Milestone 0 spike below).

## Milestone 0 spike: what was proven

Verified end-to-end in a real browser (Playwright), not just "it builds": create a
block by dragging from the toolbox, save to SQLite, reload the page, load from SQLite
(same block ID reappears), highlight it, toggle read-only. Zero console errors.

Part A (toolchain, §3b): `package.json` + `esbuild.config.mjs` bundle
`Blockly/blockly-host.ts` (+ Blockly + `blockly/msg/de`) into
`wwwroot/js/blockly-host.js` as a single IIFE `<script>` (not an ES module — simpler to
load from a server-rendered page, no import-map concerns). Wired into `dotnet build` via
two MSBuild targets in `SpaceKids.Client.fsproj` (`NpmInstall`, `BuildBlocklyHostBundle`),
both `BeforeTargets="BeforeBuild"`, both using MSBuild `Inputs`/`Outputs` so they no-op on
an unchanged tree. A fresh checkout builds with plain `dotnet build`/`dotnet publish` —
no separate npm step for a developer or CI to remember.

Part B (seam basics, §3a): `Blockly/blockly-host.ts` is the sole module that touches
`Blockly.*`; it exposes `window.spaceKids.*` (initWorkspace, destroyWorkspace,
loadWorkspace, serializeWorkspace, setReadOnly, highlightBlock, clearHighlight, plus two
spike-only helpers: `firstBlockId`, `getChangeLog`) called from F# via
`IJSRuntime.InvokeVoidAsync`/`InvokeAsync`. `SpaceKids.Client/Main.fs` is a throwaway
spike page (Save/Load/Highlight/Read-only buttons over one workspace) — it is not the
real client UI, which starts in Milestone 3. Save/Load round-trip through a real SQLite
file (`SpaceKids.Server/Persistence.fs`, a `spike_workspaces` table) via a Bolero remote
service (`WorkspaceService`/`WorkspaceRemoteHandler`) — superseded by the real
`workspaces` table design (§12) in Milestone 1.

Event filtering: `onWorkspaceChanged`'s listener only reacts to
`BLOCK_CREATE`/`BLOCK_DELETE`/`BLOCK_CHANGE`/`BLOCK_MOVE`; Blockly only ever emits one
`BLOCK_MOVE` per completed drag (not one per pointer-move frame), so no extra "is this
the end of the drag" bookkeeping was needed on top of the event-type filter. Verify with
`window.spaceKids.getChangeLog(containerId)` in the browser console while dragging a
block around versus creating/deleting one.

Part C (custom-block mutator mini-spike, §9): `Blockly/blocks.ts` implements
`sk_custom_block_def` — a definition shell block with a real mutator (gear icon) that
adds/removes one typed input (`Zahl`/Number, via `sk_custom_block_def_mutator_arg`
sub-blocks), using the modern `saveExtraState`/`loadExtraState` + `decompose`/`compose`
hooks (not the legacy XML mutation API). `window.spaceKids.publishCustomBlockSignature
(defContainerId, targetContainerId, customBlockId)` reads the live signature off a
definition block in one workspace, generates/re-registers a caller block type
(`registerCallerBlock` in `blocks.ts`), and pushes it into a *different* workspace's
toolbox via `Workspace.updateToolbox`. Exercised manually with two `<div>` containers on
the spike page: adding an input in the definition workspace and re-publishing
regenerates the caller with the new input present in the second workspace's "Eigene
Blöcke" category.

This mini-spike deliberately only implements one input type (Zahl/Number). The full typed
input set (Schiff, Wegpunkt, Ware, Anzahl, Preisgrenze, Liste) is Milestone 9 breadth work,
not a new category of risk — the mechanics (mutator, signature storage, caller
regeneration, cross-workspace toolbox push) are what needed proving now.

Verification level reached: the 0-input signature path (definition block → publish →
caller appears in the other workspace's toolbox) was verified fully automated
(Playwright), including re-publishing after the def block's name changes. Adding an
input via the gear icon was verified visually (screenshot: the mutator bubble opens
showing an "Eingaben" container with a draggable "Zahl n" block) rather than fully
scripted end-to-end — Playwright's coordinate-based interaction with Blockly's nested
mini-workspace bubble (non-standard class names, its own SVG canvas) turned into a test-
tooling problem, not a functionality question, and wasn't worth fully automating for a
Milestone 0 spike. If this needs re-verifying later (e.g. before Milestone 9), drive it
manually in a browser: place the def block, click its gear icon, drag a "Zahl" arg block
into the popup, close it, click "Signatur an Programm übergeben", and confirm the caller
in "Eigene Blöcke" now has an input socket.

## Milestone 1: persistence foundation (§12)

**Migrations:** hand-rolled numbered `.sql` files under
`SpaceKids.Server/Persistence/Migrations/`, embedded as resources and applied by
`MigrationRunner.fs` (tracked in a `schema_versions` table, idempotent — safe to call on
every startup). No EF Core/DbUp dependency added — the raw `Microsoft.Data.Sqlite` style
already used by the spike code was kept.

**Schema scope:** `0001_initial.sql` creates the full 12-table set from §12 in one
migration (`agents, api_tokens, workspaces, programs, custom_blocks,
custom_block_versions, jobs, job_logs, ship_locks, api_cache, request_queue_events,
schema_versions`), not just `workspaces`. Only `workspaces` and `schema_versions` are
load-bearing today — the rest sit empty until their own milestone starts writing to
them, and their exact columns are provisional until then. Chosen over growing the schema
one migration per milestone: the shape is fixed once, per the plan, rather than
revisited repeatedly.

**WAL + busy_timeout:** `MigrationRunner.run` sets `PRAGMA journal_mode=WAL` once,
outside any transaction (SQLite silently refuses to change `journal_mode` inside one).
`Database.openConnection` sets `PRAGMA busy_timeout=5000` on every connection, since
busy_timeout — unlike journal_mode — isn't persisted in the database file. Per §12, this
is expected to be sufficient for this project's actual concurrency level; escalate to a
single-writer-owner pattern only if `SQLITE_BUSY` failures actually show up.

**Backups:** `Persistence/Backup.fs`'s `BackupService` (a `BackgroundService`) runs
`VACUUM INTO` immediately on start, then hourly, then once more in `StopAsync` on clean
shutdown, pruning to the last 7 backup files by filename (UTC timestamp, so
lexicographic sort is chronological). VACUUM INTO — not a plain file copy — because WAL
mode means a live file copy isn't a consistent snapshot.

**Spike retirement:** Milestone 0's `spike_workspaces` table and root `Persistence.fs`
are gone. The Milestone 0 spike page (`SpaceKids.Client/Main.fs`) is unchanged and still
works — `WorkspaceRemoting.fs` now backs it with `Persistence/WorkspaceRepository.fs`
against the real `workspaces` table instead.

**Test note:** `Microsoft.Data.Sqlite` pools native connections by default, which keeps a
database file locked on Windows even after every `SqliteConnection` in a test is
disposed. Tests that delete their temp `.db` file in a `finally` block must call
`SqliteConnection.ClearAllPools()` first (see `tests/SpaceKids.Server.Tests/Tests.fs`).

## Milestone 2: real data, no Blockly yet (§19)

**Token flow: paste an existing token, not self-registration.** Confirmed with the user
rather than guessed. `docs.spacetraders.io`'s prose pages (quickstart, API authorization)
are JS-rendered — WebFetch only ever returned navigation shells across several attempts,
never real content — so the current `/register` request/response contract couldn't be
verified safely. `api.spacetraders.io/v2` itself (a plain JSON status endpoint) and
`api.spacetraders.io/v2/documentation/json` (the OpenAPI spec) are *not* JS-rendered and
fetched fine — that's how the `Agent`/`Ship`/`Contract`/`Waypoint`/`Market` field shapes
below were verified. If self-registration is wanted later, get the current `/register`
contract from the user or a real response example rather than re-attempting the docs
site.

**Field shapes:** `SpaceKids.SpaceTraders/Types.fs` defines minimal subsets of the real
schemas (verified field-by-field against the OpenAPI spec) — e.g. `Ship` only carries
`symbol`, `registration.role`, `nav.{systemSymbol,waypointSymbol,status}`,
`fuel.{current,capacity}`, not the full nav/crew/frame/reactor/engine/modules/mounts/
cargo/cooldown shape. Deserialized with `System.Text.Json` +
`PropertyNameCaseInsensitive = true` — no extra JSON library, and unmapped extra fields
in the real API's response are silently ignored, which is exactly what's wanted for a
deliberately partial subset.

**`DataEnvelope<'a>` must be public, not `internal`.** System.Text.Json's default
reflection-based converter only uses a type's constructor if the constructor itself is
accessible (public here); an `internal` record's implicit constructor made deserialization
throw `NotSupportedException` at runtime, not compile time — caught by the integration
tests immediately. If a future envelope/DTO type needs to stay non-public, it would need
a `[<JsonConstructor>]`-annotated public constructor instead.

**Market waypoint assumption:** the market fetched is always the agent's own
headquarters waypoint, not discovered via `Waypoint.traits`. True for most starting
waypoints in this game; a real limitation if a given account's HQ isn't a marketplace.
Waypoint-trait-based market discovery is a natural follow-up, not required for
Milestone 2's "done when."

**Request queue stub (§13):** `SpaceKids.Server/RequestQueue.fs` is a single static
`SemaphoreSlim(1,1)` gate wrapping every SpaceTraders call, logging one row per call to
`request_queue_events` (endpoint name, status, and — on failure — the exception message
as `response_metadata_json`). No priorities, no backoff, no aging — that's Milestone 5.
The important thing locked in now: there is no ad hoc HTTP path anywhere that bypasses
this, so nothing needs rewiring when the real queue lands.

**`SpaceKids.FakeSpaceTraders` testing setup:** F# `[<EntryPoint>] let main` doesn't
generate a public `Program` class the way C# top-level statements do, so
`WebApplicationFactory<T>` (used by `SpaceKids.IntegrationTests`) has nothing to target
by default. Fixed with the standard workaround: a marker `type Program() = class end` in
`Program.fs`, with the actual endpoint wiring factored into `App.configureApp` so both
the marker-carrying entry point and tests can use it. The fake's endpoints are mounted
at bare paths (`/my/agent`, not `/v2/my/agent`) — `WebApplicationFactory`'s default
`HttpClient` base address (`http://localhost/`) has no `/v2` segment to match, and adding
one would only be cosmetic since the client's relative paths resolve against whatever
base address it's given either way.

**Verified live, not just against the fake:** with a real user-provided SpaceTraders
token (never written to any tracked file — the app persists it only in the gitignored
local `spacekids.db`), a real `submitToken` call round-tripped against
`https://api.spacetraders.io/v2/` and returned real agent/ship/waypoint/market data, with
all 5 calls correctly logged in `request_queue_events`. An earlier version of
`AgentRemoting.fs` called `GET /my/agent` twice per `submitToken` (once to validate the
token, once again inside the shared data-loading helper) — caught during this live
verification and fixed by threading the already-fetched `Agent` through instead of
re-fetching it, since every avoidable call against a rate-limited third-party API is
worth avoiding.

## Milestone 3: Blockly in German, full integration (§19)

**Scope-reducing finding: most of §6's 14 "programming" blocks are stock Blockly, not
new custom blocks.** Only the 20 SpaceTraders-specific action/information blocks needed
new `Blockly.Blocks[type]` definitions (`SpaceKids.Client/Blockly/blocks-catalog.ts`).
`Wenn`/`Wenn sonst` are the *same* stock block (`controls_if` — "sonst" is its own
built-in mutator, not a separate type); `Wiederhole` → `controls_repeat_ext`,
`Wiederhole bis` → `controls_whileUntil`, `Für jedes Element` → `controls_forEach`,
`Vergleiche Werte` → `logic_compare`, `Rechne` → `math_arithmetic`,
`Erstelle/Füge-zu/Hole-aus Liste` → `lists_create_with`/`lists_setIndex`/
`lists_getIndex`, `Setze`/`Ändere Variable` → `variables_set`/`math_change`. All are
already registered by the existing `import "blockly/blocks"` and already German via the
existing `blockly/msg/de` locale — no new registration code, just toolbox references.
`Zeige Nachricht`/`Warte` are the Milestone 0 spike's `sk_show_message`/`sk_wait`,
reused unchanged.

**Variables use Blockly's dynamic `{kind: "variables", custom: "VARIABLE"}` toolbox
category** (auto-generates the "create variable" button plus `variables_get`/
`variables_set` blocks matching declared variables) rather than manually listing
`variables_set`. `math_change` ("Ändere Variable") isn't part of that dynamic category
in Blockly core, so it's listed explicitly in the "Programmierung" category instead.

**One toolbox for both workspaces:** `buildCatalogToolbox(callerBlockTypes)` replaced
`buildTrivialToolbox` with the same signature, so it serves both the main "Programm"
workspace and the "Blockwerkstatt" mutator-spike workspace identically — a custom
block's body should have the same primitives available as the main program, and this
avoids inventing a second toolbox variant for no reason. The Milestone 0 Part C mutator
spike (`sk_custom_block_def`, `publishCustomBlockSignature`) is completely untouched;
custom-block *calling* is still Milestone 9 scope.

**"Simulate run" is not DSL execution.** `simulateRun` (`blockly-host.ts`) walks
`getTopBlocks(true)[0]` and its `.getNextBlock()` chain, highlighting each with a 700ms
pause via the existing `highlightBlock`/`clearHighlight`. It has no notion of branches,
loops, or the DSL at all — it proves highlighting works across the full catalog ahead of
Milestone 4's real compiler/interpreter, per the milestone's own "fake/simulated run"
wording.

**Inputs are plain value sockets, not typed ones.** Every catalog block's input (e.g.
`navigate`'s `destination`) accepts any value block for now — dedicated typed sockets
(Schiff/Wegpunkt/Ware/...) are Milestone 9 scope, consistent with the existing note on
the Milestone 0 mutator spike above.

Verified in a real browser (Playwright): all 6 toolbox categories render with correct
German labels (Aktionen, Informationen, Programmierung, Variablen, Eigener Block, Eigene
Blöcke), a `navigate` block drags onto the canvas with its `Wegpunkt` input socket
visible, Speichern → reload → Laden round-trips it through the real `workspaces` table,
"Simuliere Ausführung" completes without error, and the Milestone 2 dashboard section
(unrelated to this milestone) still renders correctly on the same page. Zero console
errors. `tsc --noEmit` (the `typecheck` npm script, not wired into the build — esbuild
doesn't type-check) run explicitly to catch type errors the bundler would silently
swallow.
